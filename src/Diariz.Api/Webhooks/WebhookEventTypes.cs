namespace Diariz.Api.Webhooks;

/// <summary>Canonical outbound event-type keys (v1) and CSV membership helpers. Subscriptions store the set
/// of wanted types as a comma-separated string in <c>WebhookSubscription.EventTypes</c>.</summary>
public static class WebhookEventTypes
{
    public const string RecordingCreated = "recording.created";
    public const string RecordingTranscribed = "recording.transcribed";
    public const string RecordingTranscriptionFailed = "recording.transcription_failed";
    public const string FormulaResultCompleted = "formula_result.completed";
    public const string FormulaResultFailed = "formula_result.failed";
    public const string Ping = "webhook.ping"; // test-only, never subscribable

    /// <summary>The types a user may subscribe to (excludes the internal ping).</summary>
    public static readonly IReadOnlyList<string> Subscribable = new[]
    {
        RecordingCreated, RecordingTranscribed, RecordingTranscriptionFailed,
        FormulaResultCompleted, FormulaResultFailed,
    };

    public static string[] Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Join(IEnumerable<string> types) => string.Join(',', types);

    public static bool Matches(string? csv, string type) =>
        Split(csv).Contains(type, StringComparer.Ordinal);
}
