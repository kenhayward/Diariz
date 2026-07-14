using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The app ships a set of standard Platform meeting types (minutes templates), seeded idempotently by
/// their stable <see cref="MeetingType.Key"/> - including the "General Meeting" default that reproduces the
/// original minutes structure and is the fallback for a recording with no explicit type.
///
/// A type no longer carries its own template: it points at the <b>formula</b> that generates its minutes, which the
/// seeder creates alongside it as a built-in Diariz formula.</summary>
public class MeetingTypeSeederTests
{
    /// <summary>The template the General type currently points at (its primary formula's content).</summary>
    private static async Task<Formula> GeneralFormula(DiarizDbContext db)
    {
        var general = await db.MeetingTypes
            .Include(t => t.PrimaryFormula)
            .SingleAsync(t => t.Key == MeetingType.GeneralKey);
        return general.PrimaryFormula!;
    }

    [Fact]
    public async Task SeedAsync_creates_the_standard_platform_types()
    {
        using var db = TestDb.Create();

        await MeetingTypeSeeder.SeedAsync(db);

        var types = await db.MeetingTypes.ToListAsync();
        Assert.Equal(MeetingTypeSeeder.Standards.Count, types.Count);
        Assert.All(types, t => Assert.Null(t.UserId));                      // all Platform (shared)
        Assert.All(types, t => Assert.False(string.IsNullOrWhiteSpace(t.Key)));
        Assert.Contains(types, t => t.Key == MeetingType.GeneralKey);
    }

    // Each standard gets the formula that generates its minutes, and points at it. The formula is built-in, so it
    // can't be deleted out from under the template it drives.
    [Fact]
    public async Task SeedAsync_gives_every_standard_a_builtin_formula_and_links_it()
    {
        using var db = TestDb.Create();

        await MeetingTypeSeeder.SeedAsync(db);

        var types = await db.MeetingTypes.Include(t => t.PrimaryFormula).ToListAsync();
        Assert.All(types, t => Assert.NotNull(t.PrimaryFormula));
        Assert.All(types, t => Assert.Equal(FormulaScope.Diariz, t.PrimaryFormula!.Scope));
        Assert.All(types, t => Assert.True(t.PrimaryFormula!.IsBuiltIn));
        // A minutes template needs the transcript, the note-taker's lines, and the canonical actions.
        Assert.All(types, t => Assert.Equal(
            FormulaContext.Transcript | FormulaContext.Notes | FormulaContext.Actions, t.PrimaryFormula!.Context));
    }

    [Fact]
    public async Task SeedAsync_general_type_has_valid_non_empty_content()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);

        var content = TemplateContent.Parse((await GeneralFormula(db)).ContentJson);
        Assert.NotEmpty(content.Sections);
        Assert.True(content.Validate().Ok);
        // It renders the canonical actions table (parity with today's minutes).
        Assert.Contains(content.Sections.SelectMany(s => s.Blocks),
            b => b.Kind == TemplateBlock.FieldKind && b.Field == "action_items");
    }

    [Fact]
    public async Task SeedAsync_is_idempotent_and_preserves_admin_edits()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);

        // A platform admin renames a standard type.
        var general = await db.MeetingTypes.SingleAsync(t => t.Key == MeetingType.GeneralKey);
        general.Title = "Renamed by admin";
        await db.SaveChangesAsync();

        await MeetingTypeSeeder.SeedAsync(db);   // second boot

        Assert.Equal(MeetingTypeSeeder.Standards.Count, await db.MeetingTypes.CountAsync()); // no duplicates
        var again = await db.MeetingTypes.SingleAsync(t => t.Key == MeetingType.GeneralKey);
        Assert.Equal("Renamed by admin", again.Title); // insert-if-missing: the edit survives
    }

    [Fact]
    public async Task SeedAsync_freshGeneral_containsTheEnhancedNotesField()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);

        Assert.True(TemplateContent.Parse((await GeneralFormula(db)).ContentJson).HasField("notes"));
    }

    [Fact]
    public async Task SeedAsync_upgradesALegacyNeverEditedGeneral_ToIncludeEnhancedNotes()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);

        // Simulate a pre-notes deployment: strip the notes section back to the legacy shape. The content now lives
        // on the type's formula, so that is what gets rolled back.
        var formula = await GeneralFormula(db);
        var content = TemplateContent.Parse(formula.ContentJson);
        formula.ContentJson = (content with
        {
            Sections = content.Sections.Where(s => s.Title != "Enhanced notes").ToList(),
        }).Serialize();
        await db.SaveChangesAsync();
        Assert.False(TemplateContent.Parse((await GeneralFormula(db)).ContentJson).HasField("notes")); // legacy shape

        await MeetingTypeSeeder.SeedAsync(db); // next boot upgrades it

        Assert.True(TemplateContent.Parse((await GeneralFormula(db)).ContentJson).HasField("notes"));

        await MeetingTypeSeeder.SeedAsync(db); // idempotent
        Assert.Equal(MeetingTypeSeeder.Standards.Count, await db.MeetingTypes.CountAsync());
    }

    [Fact]
    public async Task SeedAsync_leavesAnAdminEditedGeneral_Untouched()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);

        // An admin customised the General template (any divergence from the seeds).
        var formula = await GeneralFormula(db);
        var custom = new TemplateContent(
            [new TemplateSection(1, "My custom section", [new TemplateBlock(TemplateBlock.Prompt, Text: "Write.")])]).Serialize();
        formula.ContentJson = custom;
        await db.SaveChangesAsync();

        await MeetingTypeSeeder.SeedAsync(db); // next boot

        Assert.Equal(custom, (await GeneralFormula(db)).ContentJson); // edits are sacred - no upgrade applied
    }
}
