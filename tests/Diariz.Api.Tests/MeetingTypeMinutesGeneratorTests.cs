using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The generator resolves a recording's meeting type (or the General default), reads the platform-wide
/// generation mode, substitutes the meeting's fields (incl. the canonical actions table), and runs the matching
/// strategy.</summary>
public class MeetingTypeMinutesGeneratorTests
{
    // The General fallback comes from the shipped meeting-types/*.md, so install the catalog as boot does.
    public MeetingTypeMinutesGeneratorTests() => Standards.Install();

    private static readonly IReadOnlyList<SegmentDto> Segments =
        [new(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Hello")];
    private static readonly SummarizationRequestConfig Config = new("https://llm.test/v1", "sk", "m", 60);
    private static readonly MeetingMinutesContext Context =
        new(Guid.NewGuid(), new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero), "Weekly Sync", ["Alice", "Bob"], 3_600_000);

    private static MeetingTypeMinutesGenerator Build(DiarizDbContext db, FakeMeetingMinutesClient client) =>
        new(db,
            [new PerSectionMinutesStrategy(client), new SingleCallMinutesStrategy(client)],
            new FilePromptTemplateProvider("nonexistent-prompts-dir"), client); // -> built-in preamble fallback

    private static async Task SetMode(DiarizDbContext db, MinutesGenerationMode mode)
    {
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, MinutesGenerationMode = mode });
        await db.SaveChangesAsync();
    }

    private static Guid AddType(DiarizDbContext db, Guid? owner, string sectionTitle)
    {
        var content = new TemplateContent(
            [new TemplateSection(1, sectionTitle, [new TemplateBlock(TemplateBlock.Prompt, Text: "Write it.")])]);

        var t = MeetingTypes.With(
            db, content,
            userId: owner,
            scope: owner is null ? FormulaScope.Platform : FormulaScope.Personal,
            overview: "Ctx");
        db.SaveChanges();
        return t.Id;
    }

    private static readonly IReadOnlyList<ExtractedAction> Actions = [new("Send report", "Bob", "2026-03-06")];

    [Fact]
    public async Task Null_type_uses_the_seeded_General_default_substitutes_fields_and_renders_actions()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);
        await SetMode(db, MinutesGenerationMode.PerSection);
        var client = new FakeMeetingMinutesClient { Result = "PROMPT-OUT" };

        var md = await Build(db, client).GenerateAsync(
            Guid.NewGuid(), null, Context, Segments, Actions, [], Config, 16000);

        Assert.Contains("# Meeting details", md);        // General template's structure
        Assert.Contains("Date: 2026-03-04", md);          // date field substituted from the context
        Assert.Contains("Attendees: Alice, Bob", md);     // attendees field
        Assert.Contains("PROMPT-OUT", md);                // a model-prompt block was filled
        Assert.Contains("Send report", md);               // the action_items field rendered the canonical table
    }

    [Fact]
    public async Task A_type_the_user_cannot_use_falls_back_to_General()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);
        await SetMode(db, MinutesGenerationMode.PerSection);
        var otherUsersType = AddType(db, owner: Guid.NewGuid(), sectionTitle: "SOMEONE ELSES");
        var client = new FakeMeetingMinutesClient { Result = "x" };

        var md = await Build(db, client).GenerateAsync(
            Guid.NewGuid(), otherUsersType, Context, Segments, Actions, [], Config, 16000);

        Assert.DoesNotContain("SOMEONE ELSES", md);
        Assert.Contains("# Meeting details", md);        // General used instead
    }

    [Fact]
    public async Task A_usable_platform_type_is_used()
    {
        using var db = TestDb.Create();
        await SetMode(db, MinutesGenerationMode.PerSection);
        var typeId = AddType(db, owner: null, sectionTitle: "CUSTOM SECTION");
        var client = new FakeMeetingMinutesClient { Result = "body" };

        var md = await Build(db, client).GenerateAsync(
            Guid.NewGuid(), typeId, Context, Segments, Actions, [], Config, 16000);

        Assert.Contains("# CUSTOM SECTION", md);
    }

    [Fact]
    public async Task Mode_selects_the_strategy()
    {
        // A two-prompt template: per-section makes two calls, single-call makes one.
        Guid typeId;
        using (var db = TestDb.Create())
        {
            typeId = AddTwoPromptType(db);
            await SetMode(db, MinutesGenerationMode.PerSection);
            var client = new FakeMeetingMinutesClient { Result = "x" };
            await Build(db, client).GenerateAsync(Guid.NewGuid(), typeId, Context, Segments, Actions, [], Config, 16000);
            Assert.Equal(2, client.Calls);
        }
        using (var db = TestDb.Create())
        {
            typeId = AddTwoPromptType(db);
            await SetMode(db, MinutesGenerationMode.SingleCall);
            var client = new FakeMeetingMinutesClient { Result = "x" };
            await Build(db, client).GenerateAsync(Guid.NewGuid(), typeId, Context, Segments, Actions, [], Config, 16000);
            Assert.Equal(1, client.Calls);
        }
    }

    private static Guid AddTwoPromptType(DiarizDbContext db)
    {
        var content = new TemplateContent(
        [
            new TemplateSection(1, "A", [new TemplateBlock(TemplateBlock.Prompt, Text: "one")]),
            new TemplateSection(1, "B", [new TemplateBlock(TemplateBlock.Prompt, Text: "two")]),
        ]);

        var t = MeetingTypes.With(db, content);
        db.SaveChanges();
        return t.Id;
    }
}
