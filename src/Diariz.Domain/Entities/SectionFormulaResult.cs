namespace Diariz.Domain.Entities;

/// <summary>The Markdown document produced by running a Formula over a folder (section) and its
/// sub-sections. Many per section (one per run). Mirrors <see cref="FormulaResult"/> but section-scoped.</summary>
public class SectionFormulaResult
{
    public Guid Id { get; set; }
    public Guid SectionId { get; set; }
    public Section? Section { get; set; }
    public Guid? CreatedByUserId { get; set; }     // dropped (SET NULL) if the author's account is deleted
    public ApplicationUser? CreatedBy { get; set; }
    public Guid? FormulaId { get; set; }           // SET NULL if the formula is later deleted
    public Formula? Formula { get; set; }
    public string Name { get; set; } = string.Empty;   // formula name snapshot
    public string Text { get; set; } = string.Empty;   // generated Markdown body
    public int Ordinal { get; set; }
    public FormulaRunStatus Status { get; set; } = FormulaRunStatus.Generating;
    public string? Error { get; set; }

    /// <summary>The user hand-edited this document. An <b>automatic</b> re-run (a meeting type's additional
    /// formulas, re-firing whenever the minutes regenerate) must never overwrite it - the same rule
    /// <c>MeetingMinutes.IsUserEdited</c> has always had. An <b>explicit</b> manual run does replace it, and
    /// clears this - the user asked for it.</summary>
    public bool IsUserEdited { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
