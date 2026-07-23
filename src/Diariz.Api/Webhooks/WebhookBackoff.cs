namespace Diariz.Api.Webhooks;

/// <summary>Exponential-ish retry schedule: ~8 attempts spread over ~24h (Standard-Webhooks style).</summary>
public static class WebhookBackoff
{
    public const int MaxAttempts = 8;

    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(5), TimeSpan.FromHours(10),
    };

    /// <summary>Delay before the given (1-based) attempt number. Clamped to the last entry.</summary>
    public static TimeSpan NextDelay(int attemptCount)
    {
        var i = Math.Clamp(attemptCount - 1, 0, Delays.Length - 1);
        return Delays[i];
    }
}
