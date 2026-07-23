namespace Diariz.Domain.Entities;

/// <summary>A user-registered outbound webhook ("Automation"). Fires signed HTTP events to <see cref="Url"/>
/// when the owner's recordings/formula results change state.</summary>
public class WebhookSubscription
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public ApplicationUser? Owner { get; set; }

    /// <summary>Personal (Phase 2) or Platform (Phase 3). Defaults to Personal.</summary>
    public WebhookScope Scope { get; set; } = WebhookScope.Personal;

    public string Name { get; set; } = string.Empty;

    /// <summary>Delivery target. https required (http only for localhost in dev); SSRF-validated on write.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The HMAC signing secret, encrypted at rest (Data Protection). Shown to the user once.</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    /// <summary>Comma-separated event-type keys this subscription wants (see WebhookEventTypes).</summary>
    public string EventTypes { get; set; } = string.Empty;

    /// <summary>Comma-separated Workflow Signal keys this subscription routes on. Platform subscriptions require
    /// a non-empty filter (they fire only when a signal matches); personal subscriptions may use it to narrow.</summary>
    public string? SignalFilter { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Consecutive failed deliveries; reset to 0 on any success. Auto-disable at the threshold.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Set when auto-disabled so the UI can explain why.</summary>
    public string? DisabledReason { get; set; }

    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string? LastStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
