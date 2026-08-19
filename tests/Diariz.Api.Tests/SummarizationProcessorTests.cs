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
                StartMs = 0, EndMs = 1000, Original = "Hi", Ordinal = 0
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
        var resolver = new FakeLlmSettingsResolver(); // default config Model = "test-model"
        var hub = new FakeHubContext();

        await SummarizationProcessor.ProcessAsync(db, client, resolver, hub, Job(rec, tr), SummarizationPrompt.DefaultTemplate, NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var summary = await db.Summaries.SingleAsync(s => s.TranscriptionId == tr.Id);
        Assert.Equal("The key points.", summary.Text);
        Assert.Equal("test-model", summary.Model);              // from the resolved config
        Assert.Equal(LlmCallKind.Summarize, resolver.LastKind);       // resolved for the right call kind
        Assert.Equal(resolver.Config, client.LastConfig);       // passed straight to the client

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

        await SummarizationProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(),
            new FakeHubContext(), Job(rec, tr), SummarizationPrompt.DefaultTemplate, NullLogger.Instance, new CapturingWebhookPublisher(), "");

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

        await SummarizationProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(),
            hub, Job(rec, tr), SummarizationPrompt.DefaultTemplate, NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Failed, reloaded!.Status);
        Assert.Equal("LLM down", reloaded.Error);
        Assert.Empty(await db.Summaries.ToListAsync());
        var msg = Assert.Single(hub.Sent);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_WhenShutdownCancelsMidCall_StillRecordsFailed()
    {
        // The API being stopped (redeploy, container restart) cancels the job's token while the LLM call
        // is in flight. The failure write is the only thing that moves the recording off Summarizing, so
        // it must not be made with the token that just aborted the call - a write that cancels too leaves
        // the recording stuck in Summarizing forever, with the stream entry already acked and no error
        // anywhere. The user's only escape is a re-transcribe.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId, name: "x");
        using var cts = new CancellationTokenSource();
        var client = new FakeSummarizationClient(onCall: cts.Cancel)
        {
            ThrowOnCall = new OperationCanceledException("The operation was canceled."),
        };
        var hub = new FakeHubContext();

        await SummarizationProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(),
            hub, Job(rec, tr), SummarizationPrompt.DefaultTemplate, NullLogger.Instance,
            new CapturingWebhookPublisher(), "", cts.Token);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Failed, reloaded!.Status);
    }

    [Fact]
    public async Task AbandonAsync_MarksTheRecordingFailed_RatherThanLeavingItSummarizing()
    {
        // A message past the delivery cap is dropped by StreamReclaimer to stop a poison job killing worker
        // after worker. Dropping the message is right; dropping it silently is not - the recording it was
        // for stays in Summarizing with no job left to clear it and no error to explain why.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId, name: "x");
        var hub = new FakeHubContext();

        await SummarizationProcessor.AbandonAsync(db, hub, Job(rec, tr), NullLogger.Instance);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Failed, reloaded!.Status);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.Error));
        var msg = Assert.Single(hub.Sent);
        Assert.Equal("RecordingStatusChanged", msg.Method);
        Assert.Equal(userId.ToString(), msg.Group);
    }

    [Fact]
    public async Task AbandonAsync_LeavesARecordingThatHasSinceMovedOn_Alone()
    {
        // The message may have been sitting in the pending list for hours, during which the user can have
        // re-run the summary or re-transcribed the recording. Failing it then would report a stale job's
        // death against work that has since succeeded.
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), name: "x");
        rec.Status = RecordingStatus.Summarized;
        await db.SaveChangesAsync();
        var hub = new FakeHubContext();

        await SummarizationProcessor.AbandonAsync(db, hub, Job(rec, tr), NullLogger.Instance);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarized, reloaded!.Status);
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task ProcessAsync_SkipsOverwrite_WhenSummaryIsUserEdited()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId, name: "x");
        db.Summaries.Add(new Summary
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "user", Text = "my edit", IsUserEdited = true,
        });
        await db.SaveChangesAsync();
        var client = new FakeSummarizationClient { Result = new SummaryResult("LLM-generated", "Name") };

        await SummarizationProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(),
            new FakeHubContext(), Job(rec, tr), SummarizationPrompt.DefaultTemplate, NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var summary = await db.Summaries.SingleAsync(s => s.TranscriptionId == tr.Id);
        Assert.Equal("my edit", summary.Text);   // user edit preserved
        Assert.Equal(0, client.Calls);           // LLM never called
        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarized, reloaded!.Status); // settled, not left Summarizing/Failed
    }

    [Fact]
    public async Task ProcessAsync_AttributesTheCall_ToTheRecordingAndItsOwner()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId, name: "Attributed Recording");

        LlmCallKind? observedKind = null;
        Guid? observedRecording = null;
        Guid? observedUser = null;
        var client = new FakeSummarizationClient(onCall: () =>
        {
            observedKind = LlmCallScope.Active?.Kind;
            observedRecording = LlmCallScope.Active?.RecordingId;
            observedUser = LlmCallScope.Active?.UserId;
        });

        await SummarizationProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(),
            new FakeHubContext(), Job(rec, tr), SummarizationPrompt.DefaultTemplate, NullLogger.Instance,
            new CapturingWebhookPublisher(), "");

        Assert.Equal(LlmCallKind.Summarize, observedKind);
        Assert.Equal(rec.Id, observedRecording);
        Assert.Equal(rec.UserId, observedUser);
    }

    [Fact]
    public async Task ProcessAsync_NoSegments_SetsFailed()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), name: "x", withSegments: false);
        var client = new FakeSummarizationClient();

        await SummarizationProcessor.ProcessAsync(db, client, new FakeLlmSettingsResolver(),
            new FakeHubContext(), Job(rec, tr), SummarizationPrompt.DefaultTemplate, NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Failed, reloaded!.Status);
        Assert.Equal(0, client.Calls); // never called the LLM with an empty transcript
    }
}
