namespace Diariz.Domain.Entities;

/// <summary>One event queued for delivery to one subscription. Doubles as the retry queue and the audit log.</summary>
public class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public WebhookSubscription? Subscription { get; set; }

    /// <summary>Stable idempotency key (the `webhook-id` header); constant across retries of this delivery.</summary>
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    /// <summary>The exact signed request body. Stored as plain text (NOT jsonb) so the bytes - and therefore
    /// the HMAC signature computed over them - are preserved verbatim across retries.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;
    public int AttemptCount { get; set; }

    /// <summary>Earliest time the worker may attempt this delivery. The poll key.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public int? ResponseStatus { get; set; }
    public string? LastError { get; set; }

    /// <summary>When the worker last actually contacted the target for this delivery (null until first attempt).
    /// Used to enforce the per-subscription rolling-minute delivery rate cap.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
