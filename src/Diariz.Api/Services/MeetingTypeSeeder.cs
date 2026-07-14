using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Seeds the standard Platform meeting types (minutes templates) the app ships with, together with the
/// Diariz formula that generates each one's minutes. Idempotent by <see cref="MeetingType.Key"/> and
/// <b>insert-if-missing</b> - it never overwrites an existing type, so a Platform Administrator's edits to (or
/// deletion of) a standard survive redeploys. Runs on every boot.
///
/// <para>The templates themselves are <b>markdown files</b> (<c>meeting-types/*.md</c>, loaded by
/// <see cref="MeetingTypeCatalog"/> at boot into <see cref="Standards"/>) rather than hand-built in C#. The words
/// the model is given are content, not code, and belong somewhere a reviewer can read them.</para></summary>
public static class MeetingTypeSeeder
{
    /// <summary>The standard set, loaded from <c>meeting-types/*.md</c> at boot (see <c>Program.cs</c>). Empty until
    /// then - and empty if the directory is missing, which is why <see cref="EmergencyGeneral"/> exists.</summary>
    public static IReadOnlyList<StandardMeetingType> Standards { get; private set; } = [];

    /// <summary>Install the catalog loaded from disk. Called once at boot.</summary>
    public static void UseStandards(IReadOnlyList<StandardMeetingType> standards) => Standards = standards;

    /// <summary>The template used when nothing else can be found - no chosen type, no seeded General, and no content
    /// files on disk. It is deliberately <b>not</b> a copy of the General standard (which would drift from the
    /// file): it is a minimal, obviously-generic document, so a deployment missing its content directory still
    /// produces usable minutes rather than none.</summary>
    public static TemplateContent EmergencyGeneral { get; } = TemplateMarkdown.Parse(
        """
        # Summary
        [[WRITE: Summarise the meeting in a short paragraph.]]

        # Discussion
        [[WRITE: Summarise the discussion, grouped by theme.]]

        # Decisions
        [[WRITE: List the decisions made. Omit this section if none were made.]]

        # Action items
        {{action_items}}
        """);

    /// <summary>The General template as seeded BEFORE the Enhanced-notes section existed. Kept so
    /// <see cref="SeedAsync"/> can recognise a never-edited legacy row and upgrade it - and only it.</summary>
    private static string LegacyGeneralContent() => TemplateMarkdown.Parse(
        """
        # Meeting details
        Date: {{date}}
        Time: {{time}}
        Attendees: {{attendees}}
        Duration: {{duration}}

        # Purpose
        [[WRITE: State the purpose / context of the meeting in 1-2 lines.]]

        # Discussion
        [[WRITE: Summarise the discussion grouped by theme (not chronologically), concise and decision-oriented. Omit this section if there was no substantive discussion.]]

        # Decisions
        [[WRITE: List the decisions made. Omit this section if none were made.]]

        # Open questions
        [[WRITE: List any open questions or parking-lot items. Omit this section if there are none.]]

        # Next steps
        [[WRITE: Describe the next steps or next meeting in narrative form. Omit this section if there are none.]]

        # Action items
        {{action_items}}
        """).Serialize();

    /// <summary>The formula name a standard's minutes template is seeded under. Stable, because the formula seeder
    /// is create-only and keys on Name - so an admin's edit to a standard's template survives a redeploy.</summary>
    public static string FormulaNameFor(StandardMeetingType std) => $"{std.Title} minutes";

    /// <summary>Insert any standard whose <see cref="MeetingType.Key"/> isn't already present, together with the
    /// Diariz formula that generates its minutes, and link the two. Existing rows are left untouched.
    ///
    /// <para>The formula is <c>IsBuiltIn</c>, so it cannot be deleted out from under the template it drives. It is
    /// create-only by Name, mirroring <c>Seeder.SeedFormulasAsync</c> - an admin who reworks a standard's template
    /// keeps their version.</para></summary>
    public static async Task SeedAsync(DiarizDbContext db, CancellationToken ct = default)
    {
        var have = (await db.MeetingTypes
            .Where(m => m.Key != null)
            .Select(m => m.Key!)
            .ToListAsync(ct)).ToHashSet();

        foreach (var std in Standards.Where(s => !have.Contains(s.Key)))
        {
            var formula = await EnsureFormulaAsync(db, std, ct);
            db.MeetingTypes.Add(new MeetingType
            {
                Id = Guid.NewGuid(),
                Key = std.Key,
                UserId = null,
                GroupName = std.GroupName,
                Title = std.Title,
                Icon = std.Icon,
                Color = std.Color,
                Overview = std.Overview,
                PrimaryFormulaId = formula.Id,
            });
        }

        await db.SaveChangesAsync(ct);
        await UpgradeLegacyGeneralAsync(db, ct);
    }

    /// <summary>The Diariz formula carrying a standard's minutes template, created if missing. Keyed on Name (there
    /// is no key column on Formula), matching the built-in formula seeder.</summary>
    private static async Task<Formula> EnsureFormulaAsync(
        DiarizDbContext db, StandardMeetingType std, CancellationToken ct)
    {
        var name = FormulaNameFor(std);
        var existing = await db.Formulas.FirstOrDefaultAsync(f => f.Name == name, ct);
        if (existing is not null) return existing;

        var formula = new Formula
        {
            Id = Guid.NewGuid(),
            Scope = FormulaScope.Diariz,
            OwnerUserId = null,
            Name = name,
            Description = std.Overview,
            ContentJson = std.ContentJson,
            Context = std.Context,
            Enabled = true,
            IsBuiltIn = true,
        };
        db.Formulas.Add(formula);
        await db.SaveChangesAsync(ct);
        return formula;
    }

    /// <summary>One-time additive upgrade: give the seeded General template the Enhanced notes section, but only
    /// when the admin has never edited it (content still equals the previous seed) - edits are sacred. The content
    /// lives on the type's primary formula, so that is what is compared and upgraded.
    ///
    /// Compare CANONICALLY (parse + re-serialize), not byte-wise: ContentJson is a jsonb column and Postgres
    /// re-formats stored JSON (spaces after colons/commas), so raw string equality never matches the compact
    /// serializer output. (The in-memory test provider stores strings verbatim and hides this.)</summary>
    private static async Task UpgradeLegacyGeneralAsync(DiarizDbContext db, CancellationToken ct)
    {
        var general = await db.MeetingTypes
            .Include(m => m.PrimaryFormula)
            .FirstOrDefaultAsync(m => m.Key == MeetingType.GeneralKey, ct);
        if (general?.PrimaryFormula is not { } formula) return;

        if (Canonical(formula.ContentJson) != Canonical(LegacyGeneralContent())) return;

        var current = Standards.FirstOrDefault(s => s.Key == MeetingType.GeneralKey);
        if (current is null) return; // no content files - nothing to upgrade to.

        formula.ContentJson = current.ContentJson;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Canonical form of a template-content JSON string (parse + compact re-serialize), so content
    /// comparison survives jsonb's whitespace normalization.</summary>
    private static string Canonical(string json) => TemplateContent.Parse(json).Serialize();
}

/// <summary>One standard meeting type the app ships with: its presentation (title, group, icon, colour, overview),
/// the template its seeded Diariz formula carries, and the context that formula declares.</summary>
public sealed record StandardMeetingType(
    string Key, string GroupName, string Title, string Icon, string Color, string Overview, string ContentJson,
    FormulaContext Context);
