namespace Diariz.Domain.Entities;

/// <summary>Which recordings a subscription fires for. Append only - values persist as ints.</summary>
public enum WebhookScope
{
    /// <summary>Fires only for events on recordings the subscribing user owns.</summary>
    Personal = 0,

    /// <summary>Admin-owned, signal-routed, fires across users. Phase 3.</summary>
    Platform = 1,
}
