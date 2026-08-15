namespace Diariz.Domain.Entities;

/// <summary>Per-user preferences (1:1 with <see cref="ApplicationUser"/>, shared primary key).
/// Currently holds the user's own OpenAI-compatible summarisation config; null fields fall back to
/// the server defaults.</summary>
public class UserSettings
{
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Base URL of the user's OpenAI-compatible endpoint, e.g. https://api.openai.com/v1.</summary>
    public string? SummaryApiBase { get; set; }

    /// <summary>API key, encrypted at rest (never returned to clients). Null = not set.</summary>
    public string? SummaryApiKeyEncrypted { get; set; }

    public string? SummaryModel { get; set; }

    /// <summary>Chat model's context-window size in tokens, used by the context dial. Null falls back
    /// to the server default (<c>Chat:ContextLength</c>).</summary>
    public int? ChatContextWindow { get; set; }

    /// <summary>Per-request LLM timeout in seconds, overriding the platform-wide admin setting. Null
    /// inherits (PlatformSettings.LlmTimeoutSeconds, then the server option). A user points at their own
    /// endpoint and model, so a slow local model is theirs to accommodate - the value is uncapped.</summary>
    public int? LlmTimeoutSeconds { get; set; }

    /// <summary>Master switch for chat tool calling. Null falls back to the server default
    /// (<c>Chat:ToolsEnabled</c>).</summary>
    public bool? ChatToolsEnabled { get; set; }

    /// <summary>Explicit per-tool on/off overrides, a JSON object of <c>{ "tool_name": bool }</c>. A tool
    /// absent from the map follows the server default, so tools added later default correctly. Null = no
    /// overrides.</summary>
    public string? ChatToolOverridesJson { get; set; }

    /// <summary>The user's native language (BCP-47), used as the default target when translating
    /// transcripts. Null = not set.</summary>
    public string? NativeLanguage { get; set; }

    /// <summary>The language the app UI is shown in (BCP-47). Null = follow the browser / default.</summary>
    public string? UiLanguage { get; set; }

    /// <summary>Send an OpenAI-style <c>reasoning_effort</c> on LLM requests (for reasoning models). Null
    /// falls back to the server default (<c>Summarization:ReasoningEnabled</c>).</summary>
    public bool? ReasoningEnabled { get; set; }

    /// <summary>Reasoning effort level when enabled: <c>low</c> | <c>medium</c> | <c>high</c>. Null falls back
    /// to the server default (<c>Summarization:ReasoningEffort</c>).</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Google OAuth refresh token (offline access to the user's Calendar), encrypted at rest —
    /// never returned to clients. Null = the user hasn't connected Google data access.</summary>
    public string? GoogleRefreshTokenEncrypted { get; set; }

    /// <summary>The user granted read access to their Google Calendar.</summary>
    public bool GoogleCalendarGranted { get; set; }

    /// <summary>The user has opted in to mirroring their desktop Outlook calendar into Diariz. Off by default:
    /// installing the desktop app changes nothing until this is set, and the desktop never opens Outlook until
    /// the web app reports it. This is the <b>privacy</b> switch - it gates storing meeting bodies and attendee
    /// email addresses server-side - and is deliberately separate from the per-device
    /// <see cref="OutlookCalendarSource.Enabled"/> plumbing flag. Turning it off purges every source and, by
    /// cascade, every stored event.</summary>
    public bool OutlookSyncEnabled { get; set; }

    /// <summary>Which Google calendars to consider for recording attribution + the Calendar overlay, as a
    /// JSON array of calendar ids. Null = the user hasn't chosen (fall back to the calendars they've made
    /// visible in Google + their primary).</summary>
    public string? GoogleSelectedCalendarIdsJson { get; set; }

    // ---- Profile (free-text, user-editable) ----
    public string? JobTitle { get; set; }
    public string? CompanyName { get; set; }
    public string? JobDescription { get; set; }
    public string? CompanyDescription { get; set; }

    /// <summary>The user's LinkedIn account name / handle (not a full URL).</summary>
    public string? LinkedIn { get; set; }

    /// <summary>Preferred colour theme for the web UI (default <see cref="ThemePreference.Auto"/>).</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.Auto;

    /// <summary>Where a new recording lands in the user's Personal room (default
    /// <see cref="RecordingPlacementMode.SelectedFolder"/> - the folder they had selected when they pressed
    /// Record).</summary>
    public RecordingPlacementMode RecordingPlacementMode { get; set; } = RecordingPlacementMode.SelectedFolder;

    /// <summary>The fixed folder for <see cref="RecordingPlacementMode.SpecificFolder"/>. Null in the other
    /// modes (and null = Ungrouped even in SpecificFolder mode, if the chosen folder was later deleted).</summary>
    public Guid? RecordingPlacementSectionId { get; set; }

    // ---- Recording started from a calendar event ----
    // These apply only to a take started by Join-and-record on a calendar event, where the meeting's end time
    // is known. A recording started from the Record button is unaffected.

    /// <summary>Whether such a recording ends by itself, rather than running until the user presses Stop.
    /// Off by default - stopping is the user's business unless they opt in. The two settings below are the
    /// conditions, and are inert while this is false.</summary>
    public bool CalendarAutoStopEnabled { get; set; }

    /// <summary>Minutes to keep recording past the invite's end time (default 3), so the wrap-up after a
    /// meeting nominally finishes is still captured.</summary>
    public int CalendarAutoStopAfterMinutes { get; set; } = DefaultCalendarAutoStopAfterMinutes;

    /// <summary>Seconds of continuous near-silence that also ends the recording (default 30) - the meeting
    /// broke up early. Only counts once something has been heard, so a recording started before anyone
    /// speaks is never killed at the outset.</summary>
    public int CalendarSilenceStopSeconds { get; set; } = DefaultCalendarSilenceStopSeconds;

    public const int DefaultCalendarAutoStopAfterMinutes = 3;
    public const int DefaultCalendarSilenceStopSeconds = 30;
}
