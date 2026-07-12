using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The synchronous run pipeline: load a Formula + a recording scoped to the caller, enforce
/// run-access by <see cref="FormulaScope"/>, build the context (<see cref="FormulaContextBuilder"/>),
/// call the LLM, and persist a <see cref="FormulaResult"/>.</summary>
public class FormulaRunnerTests
{
    private static async Task<(Recording rec, Transcription tr)> SeedRecordingWithTranscript(
        DiarizDbContext db, Guid userId)
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
        return (rec, tr);
    }

    private static FormulaRunner MakeRunner(
        DiarizDbContext db, FakeChatStreamClient chat, FakeSummarizationSettingsResolver resolver) =>
        new(db, chat, resolver);

    [Fact]
    public async Task RunAsync_PersonalFormulaOwnedByCaller_PersistsResultAndSendsPromptAndTranscript()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "Key Decisions", Prompt = "Summarize the key decisions made.",
            Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var chat = new FakeChatStreamClient { Tokens = ["# Key", " Decisions\n", "- Did the thing"] };
        var resolver = new FakeSummarizationSettingsResolver();
        var runner = MakeRunner(db, chat, resolver);

        var result = await runner.RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal(rec.Id, result.RecordingId);
        Assert.Equal(userId, result.CreatedByUserId);
        Assert.Equal(formula.Id, result.FormulaId);
        Assert.Equal("Key Decisions", result.Name);
        Assert.Equal("# Key Decisions\n- Did the thing", result.Text);

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(result.Text, persisted!.Text);

        Assert.NotNull(chat.LastMessages);
        Assert.Equal(2, chat.LastMessages!.Count);
        Assert.Equal("system", chat.LastMessages[0].Role);
        Assert.Equal("Summarize the key decisions made.", chat.LastMessages[0].Content);
        Assert.Equal("user", chat.LastMessages[1].Role);
        Assert.Contains("Hello there, team.", chat.LastMessages[1].Content);
    }

    [Fact]
    public async Task RunAsync_FirstResultOnARecording_GetsOrdinalZero_SecondGetsOrdinalOne()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "F", Prompt = "P", Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeSummarizationSettingsResolver());

        var first = await runner.RunAsync(userId, rec.Id, formula.Id);
        var second = await runner.RunAsync(userId, rec.Id, formula.Id);

        Assert.Equal(0, first.Ordinal);
        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PersonalFormulaOwnedByAnotherUser_ThrowsFormulaNotFoundException()
    {
        // A non-owned Personal formula is treated as "not found" rather than "access denied" so its very
        // existence isn't leaked - a disabled Platform/Diariz formula (public knowledge) still throws Access.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = otherUserId,
            Name = "Not Mine", Prompt = "P", Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeSummarizationSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(() => runner.RunAsync(userId, rec.Id, formula.Id));
    }

    [Fact]
    public async Task RunAsync_DisabledPlatformFormula_ThrowsFormulaAccessException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Platform,
            Name = "Disabled", Prompt = "P", Context = FormulaContext.Transcript, Enabled = false,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeSummarizationSettingsResolver());

        await Assert.ThrowsAsync<FormulaAccessException>(() => runner.RunAsync(userId, rec.Id, formula.Id));
    }

    [Fact]
    public async Task RunAsync_RecordingNotOwnedByCaller_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, ownerId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = callerId,
            Name = "F", Prompt = "P", Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeSummarizationSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(() => runner.RunAsync(callerId, rec.Id, formula.Id));
    }

    [Fact]
    public async Task RunAsync_UnknownFormulaId_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeSummarizationSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(
            () => runner.RunAsync(userId, rec.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task RunAsync_UnknownRecordingId_ThrowsFormulaNotFoundException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Diariz,
            Name = "F", Prompt = "P", Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var runner = MakeRunner(db, new FakeChatStreamClient(), new FakeSummarizationSettingsResolver());

        await Assert.ThrowsAsync<FormulaNotFoundException>(
            () => runner.RunAsync(userId, Guid.NewGuid(), formula.Id));
    }

    [Fact]
    public async Task RunAsync_NotConfigured_ThrowsFormulaNotConfiguredException()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecordingWithTranscript(db, userId);

        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "F", Prompt = "P", Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var resolver = new FakeSummarizationSettingsResolver
        {
            Config = new SummarizationRequestConfig("", "", "", 60), // empty ApiBase => disabled
        };
        var runner = MakeRunner(db, new FakeChatStreamClient(), resolver);

        await Assert.ThrowsAsync<FormulaNotConfiguredException>(() => runner.RunAsync(userId, rec.Id, formula.Id));
    }
}
