using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>The async recording-path processor: resolve the owner's LLM config, build the formula's context,
/// stream a completion, and flip the pre-created <see cref="FormulaResult"/> row to Ready (or Failed), then
/// notify the hub. Mirrors <see cref="SectionSummaryProcessor"/> - static, fakes + in-memory DbContext.</summary>
public class FormulaRunProcessorTests
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
            Name = "Key Decisions", Prompt = "Summarize the key decisions made.",
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
        FakeHubContext hub, FormulaRunJob job) =>
        FormulaRunProcessor.ProcessAsync(db, chat, resolver, hub, job, 48_000, NullLogger.Instance);

    [Fact]
    public async Task Recording_run_succeeds_sets_ready_with_text_and_notifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient { Tokens = ["OUT", "PUT"] };
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(FormulaRunStatus.Ready, persisted!.Status);
        Assert.Equal("OUTPUT", persisted.Text);
        Assert.Null(persisted.Error);

        // The formula prompt is the system message; the transcript flows in as the user message.
        Assert.NotNull(chat.LastMessages);
        Assert.Equal("system", chat.LastMessages![0].Role);
        Assert.Equal("Summarize the key decisions made.", chat.LastMessages[0].Content);
        Assert.Contains("Hello there, team.", chat.LastMessages[1].Content);

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
        Assert.Equal(userId.ToString(), msg.Group);
    }

    [Fact]
    public async Task Chat_error_marks_failed_with_message_and_notifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeSummarizationSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.False(string.IsNullOrEmpty(persisted.Error));

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
    }

    [Fact]
    public async Task Not_configured_marks_failed_and_never_calls_chat()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient();
        var resolver = new FakeSummarizationSettingsResolver { Config = new("", "", "m", 60) }; // disabled
        var hub = new FakeHubContext();

        await Run(db, chat, resolver, hub, new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.False(string.IsNullOrEmpty(persisted.Error));
        Assert.Equal(0, chat.Calls);

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
    }
}
