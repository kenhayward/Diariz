using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The primary formula decides <b>what the model sees</b> when the minutes are generated.
///
/// <para>The strategies used to build the transcript for themselves, so minutes always got exactly the transcript
/// and nothing else. Now the context is assembled from the primary formula's <c>FormulaContext</c> flags - exactly
/// as a Formulas-tab run of that same formula would assemble it. That is the whole point of the merge: minutes and
/// formulas are the same thing.</para>
///
/// <para><b>This changes the minutes prompt.</b> The transcript now arrives as <c>[mm:ss] Speaker: Text</c> (the
/// formula rendering) rather than <c>Speaker: Text</c>, and a formula can ask for the summary or the actions too.
/// It is pinned here so the change is deliberate and visible, not a silent drift.</para></summary>
public class MinutesContextTests
{
    private static readonly SummarizationRequestConfig Config = new("https://llm.test/v1", "sk", "m", 60);

    private static MeetingTypeMinutesGenerator Build(DiarizDbContext db, FakeMeetingMinutesClient client) =>
        new(db,
            [new SingleCallMinutesStrategy(client)],
            new FilePromptTemplateProvider("nonexistent-prompts-dir"), client);

    private static async Task<Recording> SeedRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "We shipped it.", Ordinal = 0,
        });
        db.Summaries.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "m", Text = "A short summary." });
        await db.SaveChangesAsync();
        return rec;
    }

    /// <summary>Generate the minutes for a type whose primary formula declares <paramref name="flags"/>, and return
    /// the user message the model actually received.</summary>
    private static async Task<string> UserMessageAsync(DiarizDbContext db, Recording rec, FormulaContext flags)
    {
        var type = MeetingTypes.With(
            db, new TemplateContent([new TemplateSection(1, "S", [new TemplateBlock(TemplateBlock.Prompt, Text: "Write.")])]),
            title: "T");
        await db.SaveChangesAsync();

        var formula = db.Formulas.Single(f => f.Id == type.PrimaryFormulaId);
        formula.Context = flags;
        await db.SaveChangesAsync();

        var client = new FakeMeetingMinutesClient();
        var ctx = new MeetingMinutesContext(rec.Id, DateTimeOffset.UtcNow, "R", ["Alice"], 1000);

        await Build(db, client).GenerateAsync(rec.UserId, type.Id, ctx, [], [], [], Config, 16_000);

        return client.LastMessages![1].Content;
    }

    [Fact]
    public async Task The_transcript_arrives_in_the_formula_rendering_with_timestamps()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid());

        var user = await UserMessageAsync(db, rec, FormulaContext.Transcript);

        Assert.Contains("## Transcript", user);
        Assert.Contains("[00:00] Alice: We shipped it.", user);
    }

    // A minutes template can now ask for more than the transcript - which the old pipeline could never do.
    [Fact]
    public async Task A_primary_formula_can_ask_for_the_summary_too()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid());

        var user = await UserMessageAsync(db, rec, FormulaContext.Transcript | FormulaContext.Summary);

        Assert.Contains("## Summary", user);
        Assert.Contains("A short summary.", user);
    }

    [Fact]
    public async Task What_the_formula_did_not_ask_for_is_not_sent()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid());

        var user = await UserMessageAsync(db, rec, FormulaContext.Transcript);

        Assert.DoesNotContain("## Summary", user);
    }

    // These ARE the minutes: including the Minutes context on a primary would ask the document to read itself.
    // The bit is ignored rather than producing an empty (or worse, previous-run) Minutes section.
    [Fact]
    public async Task The_Minutes_bit_is_ignored_for_a_primary_formula()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid());
        var tr = db.Transcriptions.Single(t => t.RecordingId == rec.Id);
        db.MeetingMinutes.Add(new MeetingMinutes
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "m", Text = "PREVIOUS MINUTES",
        });
        await db.SaveChangesAsync();

        var user = await UserMessageAsync(db, rec, FormulaContext.Transcript | FormulaContext.Minutes);

        Assert.DoesNotContain("PREVIOUS MINUTES", user);
    }
}
