namespace Diariz.Domain.Entities;

/// <summary>Lifecycle of a single webhook delivery attempt-set. Append only - values persist as ints.</summary>
public enum WebhookDeliveryStatus
{
    Pending = 0,   // due for delivery / retrying
    Delivered = 1, // a 2xx was received
    Failed = 2,    // exhausted the retry schedule
}
