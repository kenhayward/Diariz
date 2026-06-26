using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class SummarizationProcessorTests
{
    private static async Task<(Recording rec, Transcription tr)> Seed(
        DiarizDbContext db, Guid userId, string? name, bool withSegments = true)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Name = name,
            Status = RecordingStatus.Summarizing, BlobKey = "k"
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Text = "Hi", Ordinal = 0
            });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static SummarizationJob Job(Recording rec, Transcription tr) => new(rec.Id, tr.Id);

    [Fact]
    public async Task ProcessAsync_PersistsSummary_SetsSummarized_AndNotifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId, name: "Already Named");
        var client = new FakeSummarizationClient { Result = new SummaryResult("The key points.", "Ignored") };
        var hub = new FakeHubContext();

        await SummarizationProcessor.ProcessAsync(db, client, hub, "test-model", Job(rec, tr), NullLogger.Instance);

        var summary = await db.Summaries.SingleAsync(s => s.TranscriptionId == tr.Id);
        Assert.Equal("The key points.", summary.Text);
        Assert.Equal("test-model", summary.Model);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarized, reloaded!.Status);
        Assert.Equal("Already Named", reloaded.Name); // not overwritten
        Assert.False(client.LastNeedName);

        var msg = Assert.Single(hub.Sent);
        Assert.Equal(userId.ToString(), msg.Group);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_SetsName_WhenRecordingNameBlank()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), name: null);
        var client = new FakeSummarizationClient { Result = new SummaryResult("Summary.", "Generated Title") };

        await SummarizationProcessor.ProcessAsync(db, client, new FakeHubContext(), "m", Job(rec, tr), NullLogger.Instance);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal("Generated Title", reloaded!.Name);
        Assert.True(client.LastNeedName);
    }

    [Fact]
    public async Task ProcessAsync_OnClientError_SetsFailed_AndRecordsError()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId, name: "x");
        var client = new FakeSummarizationClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var hub = new FakeHubContext();

        await SummarizationProcessor.ProcessAsync(db, client, hub, "m", Job(rec, tr), NullLogger.Instance);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Failed, reloaded!.Status);
        Assert.Equal("LLM down", reloaded.Error);
        Assert.Empty(await db.Summaries.ToListAsync());
        var msg = Assert.Single(hub.Sent);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_NoSegments_SetsFailed()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), name: "x", withSegments: false);
        var client = new FakeSummarizationClient();

        await SummarizationProcessor.ProcessAsync(db, client, new FakeHubContext(), "m", Job(rec, tr), NullLogger.Instance);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Failed, reloaded!.Status);
        Assert.Equal(0, client.Calls); // never called the LLM with an empty transcript
    }
}
