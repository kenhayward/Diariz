using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>Verifies the <c>formula_result.*</c> webhook events fire at the exact points
/// <see cref="FormulaRunProcessor.ProcessAsync"/> flips a result to Ready/Failed - right after the existing
/// <c>NotifyFormulaStatusAsync</c> hub call. Mirrors <see cref="RecordingWebhookEmitTests"/>.</summary>
public class FormulaWebhookEmitTests
{
    private static async Task<Recording> SeedRecordingWithTranscript(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "Hello there, team.", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        return rec;
    }

    private static async Task<(Formula formula, FormulaResult result)> SeedFormulaAndResult(
        DiarizDbContext db, Guid userId, Guid recordingId)
    {
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "Key Decisions", ContentJson = TemplateContent.FromPrompt("Summarize the key decisions made.").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        var result = new FormulaResult
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, CreatedByUserId = userId,
            FormulaId = formula.Id, Name = formula.Name, Ordinal = 0,
            Status = FormulaRunStatus.Generating,
        };
        db.Formulas.Add(formula);
        db.FormulaResults.Add(result);
        await db.SaveChangesAsync();
        return (formula, result);
    }

    private static Task Run(
        DiarizDbContext db, FakeChatStreamClient chat, FakeSummarizationSettingsResolver resolver,
        FakeHubContext hub, FormulaRunJob job, CapturingWebhookPublisher publisher, string publicUrl = "") =>
        FormulaRunProcessor.ProcessAsync(
            db, chat, resolver, hub, job, 48_000, NullLogger.Instance, publisher, publicUrl);

    [Fact]
    public async Task Successful_run_publishes_formula_result_completed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient { Tokens = ["OUT", "PUT"] };
        var hub = new FakeHubContext();
        var publisher = new CapturingWebhookPublisher();

        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), publisher,
            publicUrl: "https://app.test");

        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.FormulaResultCompleted);

        var (_, owner, data, _, _) = publisher.Published.Single(p => p.EventType == WebhookEventTypes.FormulaResultCompleted);
        Assert.Equal(userId, owner);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(rec.Id.ToString(), doc.RootElement.GetProperty("recordingId").GetString());
        Assert.Equal(formula.Id.ToString(), doc.RootElement.GetProperty("formulaId").GetString());
        Assert.Equal(result.Id.ToString(), doc.RootElement.GetProperty("formulaResultId").GetString());
        Assert.Equal(nameof(FormulaRunStatus.Ready), doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            $"https://app.test/api/recordings/{rec.Id}/formula-results/{result.Id}",
            doc.RootElement.GetProperty("links").GetProperty("result").GetString());
    }

    [Fact]
    public async Task Successful_run_withNoPublicUrl_omitsResultLink()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient { Tokens = ["OUT"] };
        var hub = new FakeHubContext();
        var publisher = new CapturingWebhookPublisher();

        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), publisher, publicUrl: "");

        var (_, _, data, _, _) = publisher.Published.Single(p => p.EventType == WebhookEventTypes.FormulaResultCompleted);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("links").GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Chat_error_publishes_formula_result_failed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var hub = new FakeHubContext();
        var publisher = new CapturingWebhookPublisher();

        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), publisher);

        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.FormulaResultFailed && p.Owner == userId);
    }

    [Fact]
    public async Task Section_scoped_run_omitsResultLink_evenWithPublicUrl()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = roomId, Name = "Folder" });
        await db.SaveChangesAsync();
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "Key Decisions", ContentJson = TemplateContent.FromPrompt("Summarize the key decisions made.").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        var result = new SectionFormulaResult
        {
            Id = Guid.NewGuid(), SectionId = sectionId, CreatedByUserId = userId,
            FormulaId = formula.Id, Name = formula.Name, Ordinal = 0,
            Status = FormulaRunStatus.Generating,
        };
        db.Formulas.Add(formula);
        db.SectionFormulaResults.Add(result);
        await db.SaveChangesAsync();

        var chat = new FakeChatStreamClient();
        var hub = new FakeHubContext();
        var publisher = new CapturingWebhookPublisher();

        // No recordings in the folder -> the run fails ("No meetings with content..."), exercising the
        // section-scoped (null recordingId) Failed path.
        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(null, sectionId, result.Id, formula.Id, userId), publisher, publicUrl: "https://app.test");

        var (_, _, data, _, _) = publisher.Published.Single(p => p.EventType == WebhookEventTypes.FormulaResultFailed);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("recordingId").ValueKind);
        Assert.Equal(sectionId.ToString(), doc.RootElement.GetProperty("sectionId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("links").GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Successful_run_withAttachedActiveSignal_carriesSignalsAndPlatformData()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var signal = new WorkflowSignal { Id = Guid.NewGuid(), Key = "post-to-slack", Label = "Post to Slack", IsActive = true };
        db.WorkflowSignals.Add(signal);
        db.FormulaWorkflowSignals.Add(new FormulaWorkflowSignal { FormulaId = formula.Id, WorkflowSignalId = signal.Id });
        await db.SaveChangesAsync();
        var chat = new FakeChatStreamClient { Tokens = ["OUT", "PUT"] };
        var hub = new FakeHubContext();
        var publisher = new CapturingWebhookPublisher();

        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), publisher,
            publicUrl: "https://app.test");

        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.FormulaResultCompleted
            && p.Signals.Contains("post-to-slack")
            && p.PlatformData is not null);

        var (_, _, _, _, platformData) = publisher.Published.Single(p => p.EventType == WebhookEventTypes.FormulaResultCompleted);
        var json = System.Text.Json.JsonSerializer.Serialize(platformData);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("OUTPUT", doc.RootElement.GetProperty("output").GetString());
        Assert.Equal(rec.Name ?? rec.Title, doc.RootElement.GetProperty("recordingName").GetString());
        Assert.Equal(formula.Name, doc.RootElement.GetProperty("formulaName").GetString());
    }

    /// <summary>A publisher that always throws, standing in for a transient failure in the webhook-emission
    /// path (signal/name loading or the publish call itself) - used to prove that path can never undo an
    /// already-persisted result.</summary>
    private sealed class ThrowingWebhookPublisher : IWebhookPublisher
    {
        public Task PublishAsync(string eventType, Guid ownerUserId, object data,
            IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("webhook publish exploded");
    }

    [Fact]
    public async Task Successful_run_withThrowingWebhookPublisher_stillLeavesResultReady()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var signal = new WorkflowSignal { Id = Guid.NewGuid(), Key = "post-to-slack", Label = "Post to Slack", IsActive = true };
        db.WorkflowSignals.Add(signal);
        db.FormulaWorkflowSignals.Add(new FormulaWorkflowSignal { FormulaId = formula.Id, WorkflowSignalId = signal.Id });
        await db.SaveChangesAsync();
        var chat = new FakeChatStreamClient { Tokens = ["OUT", "PUT"] };
        var hub = new FakeHubContext();

        // The webhook publisher itself throws - this must not flip the just-persisted Ready result to Failed.
        await FormulaRunProcessor.ProcessAsync(
            db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), 48_000, NullLogger.Instance,
            new ThrowingWebhookPublisher(), "https://app.test");

        var reloaded = await db.FormulaResults.FindAsync(result.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(FormulaRunStatus.Ready, reloaded!.Status);
        Assert.Equal("OUTPUT", reloaded.Text);
        Assert.Null(reloaded.Error);
    }

    [Fact]
    public async Task Failed_run_withAttachedActiveSignal_carriesSignals()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var signal = new WorkflowSignal { Id = Guid.NewGuid(), Key = "post-to-slack", Label = "Post to Slack", IsActive = true };
        db.WorkflowSignals.Add(signal);
        db.FormulaWorkflowSignals.Add(new FormulaWorkflowSignal { FormulaId = formula.Id, WorkflowSignalId = signal.Id });
        await db.SaveChangesAsync();
        var chat = new FakeChatStreamClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var hub = new FakeHubContext();
        var publisher = new CapturingWebhookPublisher();

        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), publisher);

        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.FormulaResultFailed
            && p.Signals.Contains("post-to-slack"));
    }
}
