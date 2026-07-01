using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class MeetingMinutesProcessorTests
{
    private static readonly string Template = MeetingMinutesPrompt.DefaultTemplate;

    private static async Task<(Recording rec, Transcription tr)> Seed(
        DiarizDbContext db, Guid userId, bool withSegments = true, RecordingStatus status = RecordingStatus.Summarized)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "Named", Status = status, BlobKey = "k",
            CreatedAt = new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero),
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Hi", Ordinal = 0
            });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static MeetingMinutesJob Job(Recording rec, Transcription tr) => new(rec.Id, tr.Id);

    [Fact]
    public async Task ProcessAsync_PersistsMinutes_ReusesConfig_SubstitutesMetadata_NotifiesWithoutChangingStatus()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var client = new FakeMeetingMinutesClient { Result = "# Weekly Sync\n\nMinutes." };
        var resolver = new FakeSummarizationSettingsResolver();
        var hub = new FakeHubContext();

        await MeetingMinutesProcessor.ProcessAsync(
            db, client, resolver, hub, Job(rec, tr), Template, charBudget: 16000, NullLogger.Instance);

        var minutes = await db.MeetingMinutes.SingleAsync(m => m.TranscriptionId == tr.Id);
        Assert.Equal("# Weekly Sync\n\nMinutes.", minutes.Text);
        Assert.Equal("test-model", minutes.Model);                     // from the resolved config
        Assert.Equal(userId, resolver.LastUserId);                     // resolved for the owner
        Assert.Equal(resolver.Config, client.LastConfig);              // passed straight to the client

        // The rendered template (system turn) carries the recording's metadata.
        var system = client.LastMessages![0].Content;
        Assert.Contains("Title: Named", system);
        Assert.Contains("Meeting Date: 2026-03-04", system);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarized, reloaded!.Status);    // status untouched (no race with summary)

        var msg = Assert.Single(hub.Sent);
        Assert.Equal(userId.ToString(), msg.Group);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_UpdatesExistingMinutes()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.MeetingMinutes.Add(new MeetingMinutes
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "old", Text = "stale",
        });
        await db.SaveChangesAsync();
        var client = new FakeMeetingMinutesClient { Result = "# Fresh" };

        await MeetingMinutesProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), Template, 16000, NullLogger.Instance);

        var minutes = await db.MeetingMinutes.SingleAsync(m => m.TranscriptionId == tr.Id);
        Assert.Equal("# Fresh", minutes.Text);
    }

    [Fact]
    public async Task ProcessAsync_SkipsOverwrite_WhenMinutesUserEdited()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.MeetingMinutes.Add(new MeetingMinutes
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "user", Text = "my edit", IsUserEdited = true,
        });
        await db.SaveChangesAsync();
        var client = new FakeMeetingMinutesClient { Result = "# LLM" };

        await MeetingMinutesProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), Template, 16000, NullLogger.Instance);

        var minutes = await db.MeetingMinutes.SingleAsync(m => m.TranscriptionId == tr.Id);
        Assert.Equal("my edit", minutes.Text);  // hand edit preserved
        Assert.Equal(0, client.Calls);          // LLM never called
    }

    [Fact]
    public async Task ProcessAsync_OnClientError_DoesNotThrow_LeavesStatusAndNoMinutes()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeMeetingMinutesClient { ThrowOnCall = new InvalidOperationException("LLM down") };

        await MeetingMinutesProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), Template, 16000, NullLogger.Instance);

        Assert.Empty(await db.MeetingMinutes.ToListAsync());
        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarized, reloaded!.Status); // not marked Failed — summary/transcript still valid
    }

    [Fact]
    public async Task ProcessAsync_NoSegments_DoesNothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), withSegments: false);
        var client = new FakeMeetingMinutesClient();

        await MeetingMinutesProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), Template, 16000, NullLogger.Instance);

        Assert.Equal(0, client.Calls);
        Assert.Empty(await db.MeetingMinutes.ToListAsync());
    }
}
