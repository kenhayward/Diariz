using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The minutes weave: user notes steer every prompt section (via the shared preamble) and a
/// template's `notes` field renders the Enhanced notes section (enhancer pre-pass + deterministic
/// provenance). No notes -> prompts byte-identical to before.</summary>
public class NotesWeaveTests
{
    private static readonly Guid RecId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly IReadOnlyList<SegmentDto> Segments =
        [new(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Hello")];
    private static readonly SummarizationRequestConfig Config = new("https://llm.test/v1", "sk", "m", 60);
    private static readonly MeetingMinutesContext Context =
        new(RecId, new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero), "Weekly Sync", ["Alice"], 3_600_000);

    private static MeetingNoteDto Note(string text, long? ms = null) =>
        new(Guid.NewGuid(), text, ms, 0, DateTimeOffset.UtcNow);

    private static MeetingTypeMinutesGenerator Build(DiarizDbContext db, FakeMeetingMinutesClient client) =>
        new(db,
            [new PerSectionMinutesStrategy(client), new SingleCallMinutesStrategy(client)],
            new FilePromptTemplateProvider("nonexistent-prompts-dir"), client);

    /// <summary>PerSection mode: the composer assembles headings/fields deterministically, so a canned fake
    /// client still yields an inspectable document (SingleCall would return the fake's text verbatim).</summary>
    private static async Task SetPerSection(DiarizDbContext db)
    {
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, MinutesGenerationMode = MinutesGenerationMode.PerSection });
        await db.SaveChangesAsync();
    }

    /// <summary>A minimal personal template: one prompt section, optionally an Enhanced-notes field section.</summary>
    private static async Task<Guid> SeedType(DiarizDbContext db, bool withNotesField)
    {
        var sections = withNotesField
            ? """
              [{"level":1,"title":"Summary","blocks":[{"kind":"prompt","text":"Summarise."}]},
               {"level":1,"title":"Enhanced notes","blocks":[{"kind":"field","field":"notes"}]}]
              """
            : """[{"level":1,"title":"Summary","blocks":[{"kind":"prompt","text":"Summarise."}]}]""";
        // The template lives on the formula the type points at, so build both.
        var content = TemplateContent.Parse($$"""{"sections":{{sections}}}""");
        var type = MeetingTypes.With(db, content, title: "T", overview: "Test type.");
        await db.SaveChangesAsync();
        return type.Id;
    }

    [Fact]
    public async Task NoNotes_PromptsAreUnchanged_NoSteeringBlock()
    {
        var db = TestDb.Create();
        await SetPerSection(db);
        var typeId = await SeedType(db, withNotesField: false);
        var client = new FakeMeetingMinutesClient();

        await Build(db, client).GenerateAsync(Guid.NewGuid(), typeId, Context, Segments, [], [], Config, 16_000);

        Assert.All(client.AllMessages, msgs => Assert.DoesNotContain("NOTE-TAKER'S EMPHASIS", msgs[0].Content));
    }

    [Fact]
    public async Task Notes_SteerEveryPromptSection_ViaThePreamble()
    {
        var db = TestDb.Create();
        await SetPerSection(db);
        var typeId = await SeedType(db, withNotesField: false);
        var client = new FakeMeetingMinutesClient();

        await Build(db, client).GenerateAsync(
            Guid.NewGuid(), typeId, Context, Segments, [], [Note("Comp expectations", 61_000)], Config, 16_000);

        Assert.Single(client.AllMessages); // one prompt section, no enhancer call (no notes field)
        Assert.Contains("NOTE-TAKER'S EMPHASIS", client.AllMessages[0][0].Content);
        Assert.Contains("Comp expectations", client.AllMessages[0][0].Content);
    }

    [Fact]
    public async Task NotesField_RunsEnhancerOnce_AndSubstitutesComposedMarkdown()
    {
        var db = TestDb.Create();
        await SetPerSection(db);
        var typeId = await SeedType(db, withNotesField: true);
        var client = new FakeMeetingMinutesClient
        {
            // The enhancer call asks for JSON; the section call gets Markdown.
            Responder = msgs => msgs[0].Content.Contains("JSON array")
                ? """[{"i":0,"expansion":"Comp was covered.","timesMs":[61000]}]"""
                : "Summary body.",
        };

        var md = await Build(db, client).GenerateAsync(
            Guid.NewGuid(), typeId, Context, Segments, [], [Note("Comp expectations", 61_000)], Config, 16_000);

        Assert.Equal(2, client.Calls); // 1 enhancer + 1 section (SingleCall default mode = 1 doc call)
        Assert.Contains("**Comp expectations**", md);
        Assert.Contains($"[1:01](/recordings/{RecId}?t=61000)", md);
    }

    [Fact]
    public async Task EnhancerFailure_FallsBackToRawLines_MinutesStillProduced()
    {
        var db = TestDb.Create();
        await SetPerSection(db);
        var typeId = await SeedType(db, withNotesField: true);
        var calls = 0;
        var client = new FakeMeetingMinutesClient
        {
            Responder = msgs =>
            {
                if (msgs[0].Content.Contains("JSON array")) { calls++; throw new InvalidOperationException("llm down"); }
                return "Summary body.";
            },
        };

        var md = await Build(db, client).GenerateAsync(
            Guid.NewGuid(), typeId, Context, Segments, [], [Note("Comp expectations", 61_000)], Config, 16_000);

        Assert.Equal(1, calls);
        Assert.Contains("**Comp expectations** *(1:01)*", md); // raw fallback, stamped
        Assert.Contains("Summary body.", md);                  // the rest of the minutes survived
    }

    [Fact]
    public async Task NotesField_WithNoNotes_RendersTheNoNotesSentence_WithoutAnEnhancerCall()
    {
        var db = TestDb.Create();
        await SetPerSection(db);
        var typeId = await SeedType(db, withNotesField: true);
        var client = new FakeMeetingMinutesClient { Result = "Summary body." };

        var md = await Build(db, client).GenerateAsync(
            Guid.NewGuid(), typeId, Context, Segments, [], [], Config, 16_000);

        Assert.Equal(1, client.Calls); // only the document call
        Assert.Contains(NotesComposer.NoNotes, md);
    }
}
