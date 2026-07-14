namespace Diariz.Domain.Entities;

/// <summary>A named, reusable minutes template. A meeting type is <b>presentation + selection</b>: a title, a
/// template <see cref="GroupName"/> (for grouping in the picker), an icon + background colour, and a free-text
/// <see cref="Overview"/> that orients the model.
///
/// <para>It carries <b>no prompts of its own</b>. The document it produces is defined by its
/// <see cref="PrimaryFormulaId"/> - the formula whose template generates the minutes - plus any
/// <see cref="AdditionalFormulas"/> run alongside in the same pipeline (their results land in the Formulas tab).
/// A meeting type merely points at a formula and frames it.</para>
///
/// <para><see cref="UserId"/> is nullable: <c>null</c> = a <b>Platform</b> type (created by a Platform Administrator,
/// shared read-only to everyone, incl. the seeded standards); non-null = a user's own <b>Personal</b> type
/// (full CRUD). Users may use Platform types plus their own.</para></summary>
public class MeetingType
{
    public Guid Id { get; set; }

    /// <summary>Owner of a Personal type; <c>null</c> for a shared Platform type.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The room this template belongs to. Null = a shared Platform template (mirrors UserId null); a
    /// room id = a room's own template. Plain column - FK and the UserId retirement land in Phase 4.</summary>
    public Guid? RoomId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Stable slug for the app's seeded standard types (e.g. <c>general</c>, <c>customer</c>), so the
    /// seeder can idempotently upsert them. Null for user-created types.</summary>
    public string? Key { get; set; }

    /// <summary>Grouping label shown in the picker (e.g. "Standard", "Customer meetings").</summary>
    public string GroupName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Context describing the meeting, prepended to every model prompt so it orients the model.</summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>Icon key from the app's fixed icon set (rendered client-side).</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Background colour (hex) the icon is shown on.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>The formula whose template generates the minutes. <c>null</c> falls back to the seeded General
    /// type's formula. <b>ON DELETE RESTRICT</b>: a formula in use as a primary can't be deleted or disabled out
    /// from under its templates - the alternative (SET NULL) would silently degrade everyone's minutes with no
    /// signal.</summary>
    public Guid? PrimaryFormulaId { get; set; }
    public Formula? PrimaryFormula { get; set; }

    /// <summary>Formulas run alongside the minutes in the same pipeline; their results appear in the recording's
    /// Formulas tab. Ordered by <see cref="MeetingTypeFormula.Ordinal"/>.</summary>
    public ICollection<MeetingTypeFormula> AdditionalFormulas { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The seeded "General Meeting" default that reproduces the original minutes structure; a recording
    /// with no explicit type resolves to this.</summary>
    public const string GeneralKey = "general";
}
