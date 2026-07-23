namespace Diariz.Api.Webhooks;

/// <summary>CSV helpers for a subscription's <c>SignalFilter</c> (the set of Workflow Signal keys it routes on).</summary>
public static class WebhookSignals
{
    public static string[] Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Join(IEnumerable<string> keys) => string.Join(',', keys);

    public static bool IsEmpty(string? csv) => Split(csv).Length == 0;

    /// <summary>True when any key in <paramref name="csv"/> appears in <paramref name="signals"/>.</summary>
    public static bool Intersects(string? csv, IReadOnlyList<string> signals)
    {
        if (signals.Count == 0) return false;
        var filter = Split(csv);
        return filter.Length != 0 && filter.Any(k => signals.Contains(k, StringComparer.Ordinal));
    }
}
