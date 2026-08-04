namespace Diariz.Api.Webhooks;

/// <summary>Canonical outbound event-type keys (v1) and CSV membership helpers. Subscriptions store the set
/// of wanted types as a comma-separated string in <c>WebhookSubscription.EventTypes</c>.</summary>
public static class WebhookEventTypes
{
    public const string RecordingCreated = "recording.created";
    public const string RecordingTranscribed = "recording.transcribed";
    public const string RecordingTranscriptionFailed = "recording.transcription_failed";
    public const string RecordingSummarized = "recording.summarized";
    public const string RecordingMinutesReady = "recording.minutes_ready";
    public const string RecordingActionItemsReady = "recording.action_items_ready";
    public const string RecordingTagsReady = "recording.tags_ready";
    public const string FormulaResultCompleted = "formula_result.completed";
    public const string FormulaResultFailed = "formula_result.failed";

    /// <summary>A user submitted feedback. PLATFORM SUBSCRIPTIONS ONLY - deliberately absent from
    /// <see cref="Subscribable"/>, which is the personal list. A personal subscription receives events
    /// about its owner's own data; feedback is readable only by a Platform Administrator, so a personal
    /// subscription on this type would deliver another user's words to them.</summary>
    public const string FeedbackSubmitted = "feedback.submitted";

    public const string Ping = "webhook.ping"; // test-only, never subscribable

    /// <summary>The types a user may subscribe to (excludes the internal ping).</summary>
    public static readonly IReadOnlyList<string> Subscribable = new[]
    {
        RecordingCreated, RecordingTranscribed, RecordingTranscriptionFailed,
        RecordingSummarized, RecordingMinutesReady, RecordingActionItemsReady, RecordingTagsReady,
        FormulaResultCompleted, FormulaResultFailed,
    };

    /// <summary>The types a PLATFORM subscription may choose: everything personal subscriptions may have,
    /// plus the platform-only types. Kept separate from <see cref="Subscribable"/> because that list gates
    /// PERSONAL subscriptions, which deliver events about their owner's own data - and a personal
    /// subscription on <see cref="FeedbackSubmitted"/> would deliver another user's words to them.</summary>
    public static readonly IReadOnlyList<string> PlatformSubscribable = Subscribable.Append(FeedbackSubmitted).ToArray();

    /// <summary>Whether a PLATFORM subscription must match one of the event's Workflow Signals to receive
    /// <paramref name="type"/>. True for everything except the types listed below, and deliberately so: a
    /// platform subscription is broad-reach (it fires across all users), and signals are what scope it -
    /// <c>PlatformWebhooksController</c> will not even create one without a signal.
    ///
    /// <para>Some event types have no signals concept at all, though: nothing in the product attaches a signal
    /// to <see cref="FeedbackSubmitted"/>, so the gate can never open for it and the type would be
    /// subscribable but permanently undeliverable. Those types are exempted HERE, by type - never by relaxing
    /// the gate in <see cref="Services.WebhookPublisher"/> to let an empty filter through, which would widen
    /// every existing signal-routed type to platform subscriptions that do not receive it today.</para>
    ///
    /// <para>So: adding a new platform-only event type that carries no signals means adding it here, or it
    /// will silently deliver to nobody.</para></summary>
    public static bool IsSignalRouted(string type) => type != FeedbackSubmitted;

    public static string[] Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Join(IEnumerable<string> types) => string.Join(',', types);

    public static bool Matches(string? csv, string type) =>
        Split(csv).Contains(type, StringComparer.Ordinal);
}
