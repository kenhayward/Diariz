namespace Diariz.Domain.Entities;

/// <summary>A saved prompt + a chosen context, run over a recording to produce a Markdown Result.
/// Personal formulas have an OwnerUserId; Platform/Diariz have none. Diariz-provided formulas are seeded
/// (IsBuiltIn) and cannot be deleted.</summary>
public class Formula
{
    public Guid Id { get; set; }
    public FormulaScope Scope { get; set; }
    public Guid? OwnerUserId { get; set; }         // set only for Personal
    public ApplicationUser? Owner { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public FormulaContext Context { get; set; }
    public bool Enabled { get; set; } = true;      // Platform/Diariz availability
    public bool IsBuiltIn { get; set; }            // Diariz-seeded; blocks delete
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
