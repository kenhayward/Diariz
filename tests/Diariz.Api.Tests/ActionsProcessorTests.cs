using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class ActionsProcessorTests
{
    private static readonly string Template = ActionsPrompt.DefaultTemplate;

    private static async Task<(Recording rec, Transcription tr)> Seed(
        DiarizDbContext db, Guid userId, bool withSegments = true, DateTimeOffset? actionsExtractedAt = null,
        RecordingStatus status = RecordingStatus.Summarized)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "Named", Status = status, BlobKey = "k",
            CreatedAt = new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero),
            ActionsExtractedAt = actionsExtractedAt,
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Bob will send the report by Friday.", Ordinal = 0
            });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static ActionsJob Job(Recording rec, Transcription tr) => new(rec.Id, tr.Id);

    [Fact]
    public async Task ProcessAsync_ExtractsActions_MarksExtracted_ReusesConfig_Notifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var client = new FakeActionsClient { Result = { new ExtractedAction("Send the report", "Bob", "Friday") } };
        var resolver = new FakeSummarizationSettingsResolver();
        var hub = new FakeHubContext();

        await ActionsProcessor.ProcessAsync(db, client, resolver, hub, Job(rec, tr), Template, NullLogger.Instance);

        var action = await db.RecordingActions.SingleAsync(a => a.RecordingId == rec.Id);
        Assert.Equal("Send the report", action.Text);
        Assert.Equal("Bob", action.Actor);
        Assert.Equal("Friday", action.Deadline);
        Assert.Equal(userId, resolver.LastUserId);            // resolved for the owner
        Assert.Equal(resolver.Config, client.LastConfig);     // passed straight to the client
        Assert.Equal(rec.CreatedAt, client.LastMeetingDate);  // meeting date supplied for deadline resolution

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.NotNull(reloaded!.ActionsExtractedAt);          // marks the recording as extracted
        Assert.Equal(RecordingStatus.Summarized, reloaded.Status); // status untouched (parallel with summary)

        var msg = Assert.Single(hub.Sent);
        Assert.Equal(userId.ToString(), msg.Group);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_SkipsWhenAlreadyExtracted_DoesNotClobberUserEdits()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), actionsExtractedAt: DateTimeOffset.UtcNow);
        db.RecordingActions.Add(new RecordingAction
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "My own task", Actor = "", Deadline = "", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        var client = new FakeActionsClient { Result = { new ExtractedAction("LLM task", "", "") } };

        await ActionsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance);

        var action = await db.RecordingActions.SingleAsync(a => a.RecordingId == rec.Id);
        Assert.Equal("My own task", action.Text);  // untouched
        Assert.Equal(0, client.Calls);             // LLM never called
    }

    [Fact]
    public async Task ProcessAsync_SkipsWhenLlmNotConfigured()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeActionsClient { Result = { new ExtractedAction("x", "", "") } };
        // Empty ApiBase => Config.Enabled is false (no LLM endpoint at user or server level).
        var resolver = new FakeSummarizationSettingsResolver { Config = new SummarizationRequestConfig("", "", "", 30) };

        await ActionsProcessor.ProcessAsync(db, client, resolver, new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance);

        Assert.Equal(0, client.Calls);
        Assert.Empty(await db.RecordingActions.ToListAsync());
    }

    [Fact]
    public async Task ProcessAsync_OnClientError_DoesNotThrow_LeavesStatusAndNoActions()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeActionsClient { ThrowOnCall = new InvalidOperationException("LLM down") };

        await ActionsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance);

        Assert.Empty(await db.RecordingActions.ToListAsync());
        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarized, reloaded!.Status);
        Assert.Null(reloaded.ActionsExtractedAt); // not marked extracted on failure — a re-run can retry
    }

    [Fact]
    public async Task ProcessAsync_NoSegments_DoesNothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), withSegments: false);
        var client = new FakeActionsClient();

        await ActionsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance);

        Assert.Equal(0, client.Calls);
        Assert.Empty(await db.RecordingActions.ToListAsync());
    }
}
