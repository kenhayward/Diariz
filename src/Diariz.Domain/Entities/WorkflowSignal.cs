namespace Diariz.Domain.Entities;

/// <summary>An admin-defined named routing key a formula author can attach to a formula ("Send to Slack").
/// When the formula finishes, the signal's <see cref="Key"/> rides outward on the webhook event and a
/// platform subscription filtering on that key receives it.</summary>
public class WorkflowSignal
{
    public Guid Id { get; set; }

    /// <summary>Stable machine-facing routing slug, unique (e.g. <c>post-to-slack</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Friendly, author-facing label (e.g. "Send to Slack").</summary>
    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Inactive signals are hidden from the author picker but existing links are kept.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
