namespace Diariz.Domain.Entities;

/// <summary>A saved template + a chosen context, run over a recording to produce a Markdown Result.
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

    /// <summary>The structured template as JSON (see <c>TemplateContent</c>) - the same shape a meeting type's
    /// minutes template uses. A formula that is just a prompt is stored as one headless (level-0) section holding
    /// one prompt block, which composes to exactly that prompt's output.</summary>
    public string ContentJson { get; set; } = string.Empty;

    public FormulaContext Context { get; set; }
    public bool Enabled { get; set; } = true;      // Platform/Diariz availability
    /// <summary>Only meaningful for Personal scope: when true, other users can discover this formula and
    /// subscribe to it (a live link, not a copy). Deleting the formula cascade-removes their subscriptions.</summary>
    public bool Shared { get; set; }
    public bool IsBuiltIn { get; set; }            // Diariz-seeded; blocks delete
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
