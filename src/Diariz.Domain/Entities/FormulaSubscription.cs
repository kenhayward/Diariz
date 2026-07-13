namespace Diariz.Domain.Entities;

/// <summary>A user's link to another user's shared Personal formula (a live pointer, not a copy). Lets the
/// subscriber run it and see it under "Shared Formulas" in the run picker; the owner's edits propagate.
/// Deleting the formula OR the subscriber cascade-removes the link. Unique per (FormulaId, UserId).</summary>
public class FormulaSubscription
{
    public Guid Id { get; set; }
    public Guid FormulaId { get; set; }
    public Formula? Formula { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
