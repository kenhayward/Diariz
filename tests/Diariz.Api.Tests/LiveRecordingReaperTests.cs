using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>
/// A live capture whose client vanished - a closed lid, a killed tab, a crashed browser - would
/// otherwise sit open forever. Collecting it is a strict improvement on today, where the same event
/// loses the entire meeting.
/// </summary>
public class LiveRecordingReaperTests
{
    private static readonly TimeSpan Abandon = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RecordingStatus.Live, 31, true)]
    [InlineData(RecordingStatus.Live, 29, false)]
    // Exactly at the threshold is NOT yet abandoned. Without this case the comparison could be
    // > or >= and no test would notice - which is the whole boundary the setting describes.
    [InlineData(RecordingStatus.Live, 30, false)]
    // Merging means finalise is already in flight. Reaping it would race the worker callback, which
    // is the one failure mode here that could actually corrupt a recording.
    [InlineData(RecordingStatus.Merging, 31, false)]
    [InlineData(RecordingStatus.Transcribed, 31, false)]
    [InlineData(RecordingStatus.Failed, 31, false)]
    public void IsAbandoned_Cases(RecordingStatus status, int minutesSinceLastChunk, bool expected) =>
        Assert.Equal(expected, LiveRecordingSweep.IsAbandoned(
            status, Now.AddMinutes(-minutesSinceLastChunk), Now, Abandon));

    [Fact]
    public void IsAbandoned_WithNoChunkYet_MeasuresFromTheRecordingsOwnStart()
    {
        // A capture that never sent a single chunk still has to be collected, so the caller passes
        // CreatedAt in place of a newest-chunk timestamp. Nothing special happens here - the point is
        // that a null-free contract keeps the decision pure and total.
        Assert.True(LiveRecordingSweep.IsAbandoned(RecordingStatus.Live, Now.AddMinutes(-45), Now, Abandon));
        Assert.False(LiveRecordingSweep.IsAbandoned(RecordingStatus.Live, Now.AddMinutes(-5), Now, Abandon));
    }

    [Fact]
    public async Task Sweep_AbandonedWithChunks_FinalisesRatherThanDeleting()
    {
        // The audio that did arrive is the user's. Discarding it would reproduce exactly the loss
        // this feature exists to prevent.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var rec = await SeedLive(db, me, chunkAgeMinutes: 45, chunks: 2);
        var storage = new FakeAudioStorage();
        var queue = new FakeJobQueue();

        await LiveRecordingSweep.RunAsync(db, storage, queue, new FakeHubContext(), Now, Abandon,
            NullLogger.Instance);

        Assert.Equal(RecordingStatus.Merging, (await db.Recordings.SingleAsync(r => r.Id == rec)).Status);
        var job = Assert.Single(queue.AudioMergeEnqueued);
        Assert.Equal("live-chunks", job.Kind);
        Assert.Equal(2, job.BlobKeys.Count);
    }

    [Fact]
    public async Task Sweep_AbandonedWithNoChunks_DeletesTheRecording()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var rec = await SeedLive(db, me, chunkAgeMinutes: 45, chunks: 0);

        await LiveRecordingSweep.RunAsync(db, new FakeAudioStorage(), new FakeJobQueue(),
            new FakeHubContext(), Now, Abandon, NullLogger.Instance);

        Assert.False(await db.Recordings.AnyAsync(r => r.Id == rec));
    }

    [Fact]
    public async Task Sweep_LeavesAStillActiveCaptureAlone()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var rec = await SeedLive(db, me, chunkAgeMinutes: 2, chunks: 3);
        var queue = new FakeJobQueue();

        await LiveRecordingSweep.RunAsync(db, new FakeAudioStorage(), queue, new FakeHubContext(),
            Now, Abandon, NullLogger.Instance);

        Assert.Equal(RecordingStatus.Live, (await db.Recordings.SingleAsync(r => r.Id == rec)).Status);
        Assert.Empty(queue.AudioMergeEnqueued);
    }

    [Fact]
    public async Task Sweep_WithAGap_FinalisesWhatArrivedRatherThanStranding()
    {
        // Nobody is left to retry the missing chunk, so refusing here would strand the recording in
        // Live forever - the opposite of the point. The interactive endpoint still refuses a gap;
        // only the reaper accepts one, because it is the last resort.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var rec = await SeedLive(db, me, chunkAgeMinutes: 45, sequences: [0, 2]);
        var queue = new FakeJobQueue();

        await LiveRecordingSweep.RunAsync(db, new FakeAudioStorage(), queue, new FakeHubContext(),
            Now, Abandon, NullLogger.Instance);

        Assert.Equal(RecordingStatus.Merging, (await db.Recordings.SingleAsync(r => r.Id == rec)).Status);
        Assert.Equal(2, Assert.Single(queue.AudioMergeEnqueued).BlobKeys.Count);
    }

    [Fact]
    public async Task Sweep_OneBadRowDoesNotAbortTheRest()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var first = await SeedLive(db, me, chunkAgeMinutes: 45, chunks: 1);
        var second = await SeedLive(db, me, chunkAgeMinutes: 45, chunks: 1);
        var queue = new ThrowsOnceJobQueue();

        await LiveRecordingSweep.RunAsync(db, new FakeAudioStorage(), queue, new FakeHubContext(),
            Now, Abandon, NullLogger.Instance);

        // The failing row is logged and skipped; the sweep still reaches the other one.
        Assert.Single(queue.AudioMergeEnqueued);
        var statuses = await db.Recordings.Where(r => r.Id == first || r.Id == second)
            .Select(r => r.Status).ToListAsync();
        // And the one that failed is put back to Live rather than stranded at Merging - IsAbandoned
        // excludes Merging, so a stranded row would never be retried by any later sweep.
        Assert.Contains(RecordingStatus.Live, statuses);
        Assert.Contains(RecordingStatus.Merging, statuses);
    }

    /// <summary>Fails the first audio-merge enqueue, then behaves. `new` on FakeJobQueue would not do
    /// this: the sweep calls through IJobQueue, so a hidden method would never be reached.</summary>
    private sealed class ThrowsOnceJobQueue : IJobQueue
    {
        public List<AudioMergeJob> AudioMergeEnqueued { get; } = new();
        private bool _thrown;

        public Task EnqueueAudioMergeAsync(AudioMergeJob job, CancellationToken ct = default)
        {
            if (!_thrown) { _thrown = true; throw new InvalidOperationException("redis down"); }
            AudioMergeEnqueued.Add(job);
            return Task.CompletedTask;
        }

        public Task EnqueueLiveChunkAsync(LiveChunkJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueAsync(TranscriptionJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueSummarizationAsync(SummarizationJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueMeetingMinutesAsync(MeetingMinutesJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueActionsAsync(ActionsJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueVoiceprintAsync(VoiceprintJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueEmbeddingAsync(EmbeddingJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueTagsAsync(TagsJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueSectionSummaryAsync(SectionSummaryJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueSectionMinutesAsync(SectionMinutesJob job, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueFormulaRunAsync(FormulaRunJob job, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<Guid> SeedLive(
        DiarizDbContext db, Guid userId, int chunkAgeMinutes, int chunks = 0, int[]? sequences = null)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "Live take",
            Status = RecordingStatus.Live,
            BlobKey = "",
            LiveSessionId = Guid.NewGuid(),
            CreatedAt = Now.AddMinutes(-chunkAgeMinutes),
        };
        db.Recordings.Add(rec);
        foreach (var s in sequences ?? Enumerable.Range(0, chunks).ToArray())
            db.RecordingChunks.Add(new RecordingChunk
            {
                Id = Guid.NewGuid(),
                RecordingId = rec.Id,
                Sequence = s,
                BlobKey = $"{userId}/{rec.Id}/chunks/{s:D5}.webm",
                StartMs = s * 30_000,
                EndMs = (s + 1) * 30_000,
                SizeBytes = 1000,
                ReceivedAt = Now.AddMinutes(-chunkAgeMinutes),
            });
        await db.SaveChangesAsync();
        return rec.Id;
    }
}
