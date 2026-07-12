namespace Diariz.Domain.Entities;

/// <summary>The Markdown document produced by running a Formula over a recording. Many per recording.</summary>
public class FormulaResult
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }
    public Guid? CreatedByUserId { get; set; }     // dropped (SET NULL) if the author's account is deleted
    public ApplicationUser? CreatedBy { get; set; }
    public Guid? FormulaId { get; set; }           // SET NULL if the formula is later deleted
    public Formula? Formula { get; set; }
    public string Name { get; set; } = string.Empty;   // formula name snapshot
    public string Text { get; set; } = string.Empty;   // generated Markdown body
    public int Ordinal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
