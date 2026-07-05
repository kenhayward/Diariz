using Diariz.Domain.Entities;

namespace Diariz.Api.Contracts;

// ---- Auth ----
public record LoginRequest(string Email, string Password);
public record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt);

// ---- Access requests / account setup ----
public record RequestAccessRequest(string Email, string? FullName = null);
/// <summary>Whether a setup link is valid (and, if so, the email + any pre-filled name it's for) — neutral when invalid.</summary>
public record SetupValidateResponse(bool Valid, string? Email, string? FullName = null);
public record SetupRequest(string Email, string Token, string FullName, string Password);

/// <summary>Which Google data scopes the signed-in user wants to grant (incremental consent).</summary>
public record ConnectGoogleRequest(bool Calendar);

// ---- Admin user management ----
public record AdminUserDto(
    Guid Id, string Email, string? FullName, string AccountType, UserStatus Status, bool IsEnabled,
    long QuotaBytes, long UsedBytes, bool HasGoogle = false);
public record AddUserRequest(string Email, string? FullName = null);
public record SetQuotaRequest(long QuotaBytes);
public record SetRoleRequest(string Role);
public record SetEnabledRequest(bool IsEnabled);
/// <summary>Result of granting access: whether the link was emailed, and (on the no-SMTP fallback)
/// the setup URL for the admin to share manually.</summary>
public record GrantResultDto(bool Emailed, string? SetupUrl);

// ---- Platform settings & storage quotas ----
/// <summary>Platform-wide storage-quota defaults (bytes), edited by the Platform Administrator.</summary>
public record PlatformSettingsDto(long StarterQuotaBytes, long MaxQuotaBytes);
public record UpdatePlatformSettingsRequest(long StarterQuotaBytes, long MaxQuotaBytes);
/// <summary>The signed-in user's storage usage vs their quota (bytes), plus the total wall-clock time
/// spent transcribing all their recordings (ms, summed across every transcription version).</summary>
public record StorageUsageDto(long UsedBytes, long QuotaBytes, long TotalTranscriptionMs = 0);

// ---- Sections ----
/// <summary>A user section. <c>ParentId</c> is null for a top-level section, or the id of the parent
/// section for a sub-section (one level of nesting). <c>Position</c> is the manual order among siblings.</summary>
public record SectionDto(Guid Id, string Name, Guid? ParentId, int Position);
public record CreateSectionRequest(string Name, Guid? ParentId = null);
public record RenameSectionRequest(string Name);
public record MoveRecordingRequest(Guid? SectionId);
/// <summary>Reorder/move: set each listed recording's section and position (0..n-1) in one call.</summary>
public record ReorderRecordingsRequest(Guid? SectionId, IReadOnlyList<Guid> OrderedIds);
/// <summary>Reorder/reparent sections: set each listed section's parent and position (0..n-1) in one
/// call. <c>ParentId</c> null = top level. Reparenting under a section that itself has a parent is rejected.</summary>
public record ReorderSectionsRequest(Guid? ParentId, IReadOnlyList<Guid> OrderedIds);

// ---- Recordings ----
public record RecordingSummaryDto(
    Guid Id,
    string Title,
    string? Name,
    RecordingSource Source,
    long DurationMs,
    RecordingStatus Status,
    DateTimeOffset CreatedAt,
    Guid? SectionId,
    string? SectionName,
    bool HasActions,
    bool HasAudio,
    /// <summary>The linked Google Calendar event id, or null when unlinked. Presence drives the list's
    /// calendar icon; the value lets the Calendar tab dedupe a recording against its own event.</summary>
    string? CalendarEventId = null,
    /// <summary>The linked calendar's Google colour (hex), for tinting the list's calendar icon. Null when
    /// unlinked or unknown.</summary>
    string? CalendarColor = null);

/// <summary>A recording's persisted link to a Google Calendar event (the stored snapshot). The rich invite
/// details are fetched live via <c>GET /api/calendar/events/{eventId}</c>. <see cref="CalendarId"/> targets
/// the calendar the event lives on (primary or a secondary/shared/subscribed one).</summary>
public record CalendarLinkDto(
    string EventId, string CalendarId, string? Summary, DateTimeOffset Start, DateTimeOffset End,
    string? HtmlLink, bool LinkedManually, string? Color = null);

/// <summary>Link a recording to a calendar event. <paramref name="Manual"/> = the user picked it by hand
/// (vs. the auto-saved best time-overlap match). <paramref name="CalendarId"/> is optional - when omitted the
/// server finds which calendar the event is on.</summary>
public record LinkCalendarRequest(string EventId, bool Manual = false, string? CalendarId = null);

/// <summary>Bulk delete the audio blobs of the listed recordings (keeps their transcripts/metadata).</summary>
public record DeleteAudioRequest(IReadOnlyList<Guid> Ids);

/// <summary>Merge the listed recordings' transcripts (and audio) into the earliest-created one.</summary>
public record MergeRecordingsRequest(IReadOnlyList<Guid> Ids);

public record SegmentDto(
    Guid Id, string Speaker, string SpeakerDisplay, long StartMs, long EndMs,
    string Original, string? Revised = null)
{
    /// <summary>The text shown/exported: the user's revision (or translation) when present, else the
    /// model's original. Server-side consumers (formatters, email, chat, summarisation) read this.</summary>
    public string Text => Revised ?? Original;
}

public record SummaryDto(string Model, string Text, DateTimeOffset CreatedAt, bool IsUserEdited = false);

/// <summary>Manually create or edit a transcript's summary (the current transcription version).</summary>
public record UpdateSummaryRequest(string Text);

/// <summary>LLM-generated (or hand-edited) meeting minutes for the current transcription — GitHub-flavoured
/// Markdown.</summary>
public record MeetingMinutesDto(string Model, string Text, DateTimeOffset CreatedAt, bool IsUserEdited = false);

/// <summary>Manually create or edit the current transcript's meeting minutes (Markdown).</summary>
public record UpdateMeetingMinutesRequest(string Text);

/// <summary>Email the meeting minutes to the signed-in user, optionally attaching the recording's files.</summary>
public record EmailMeetingMinutesRequest(bool IncludeAttachments = false);

public record TranscriptionDto(
    Guid Id,
    string Model,
    int Version,
    string? Language,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SegmentDto> Segments,
    long? ProcessingMs = null);

public record RecordingDetailDto(
    Guid Id,
    string Title,
    string? Name,
    RecordingSource Source,
    long DurationMs,
    long SizeBytes,
    RecordingStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    int? MinSpeakers,
    int? MaxSpeakers,
    IReadOnlyDictionary<string, string> SpeakerNames,
    IReadOnlyList<SpeakerInfoDto> Speakers,
    TranscriptionDto? Current,
    SummaryDto? Summary,
    MeetingMinutesDto? MeetingMinutes,
    IReadOnlyList<RecordingActionDto> Actions,
    bool ActionsExtracted,
    bool HasAudio,
    /// <summary>The persisted Google Calendar link (snapshot), or null when unlinked.</summary>
    CalendarLinkDto? CalendarLink = null);

public record RenameSpeakerRequest(string Label, string DisplayName);

// ---- Attachments (supporting documents/URLs on a recording) ----
/// <summary>A recording attachment: an uploaded file (with content type + size) or a URL.</summary>
public record AttachmentDto(
    Guid Id, AttachmentKind Kind, string Name, string? ContentType, long SizeBytes, string? Url, int Ordinal);
/// <summary>Attach a URL (address + optional display name) to a recording.</summary>
public record AddUrlAttachmentRequest(string Url, string? Name);
/// <summary>Create a Markdown attachment from text content (used by the chat "add as attachment" tool).</summary>
public record AddMarkdownAttachmentRequest(string Name, string Content);
/// <summary>Rename an attachment.</summary>
public record RenameAttachmentRequest(string Name);

// ---- Action items (extracted from a transcript; user-editable) ----
// Completed/CompletedAt default so export/chat projections that don't track completion stay unchanged;
// the detail + actions-list projections pass the real values.
public record RecordingActionDto(
    Guid Id, string Text, string Actor, string Deadline, int Ordinal,
    bool Completed = false, DateTimeOffset? CompletedAt = null);
public record CreateRecordingActionRequest(string? Text, string? Actor, string? Deadline);
public record UpdateRecordingActionRequest(string? Text, string? Actor, string? Deadline);

/// <summary>An action across the user's whole library, carrying its source recording (id + display name)
/// so the Actions tab can link back to the transcript.</summary>
public record ActionListItemDto(
    Guid Id, Guid RecordingId, string RecordingName, string Text, string Actor, string Deadline,
    int Ordinal, bool Completed, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);
/// <summary>Mark a set of actions complete (or not). Ids not owned by the caller are ignored.</summary>
public record CompleteActionsRequest(IReadOnlyList<Guid> Ids, bool Completed);

// ---- Speaker identification (voiceprints) ----
public record SpeakerProfileDto(Guid Id, string Name, int SampleCount);
public record CreateSpeakerProfileRequest(string Name, Guid RecordingId, string Label);
public record AssignSpeakerRequest(Guid? ProfileId);
public record RenameSpeakerProfileRequest(string Name);
public record MergeSpeakerProfilesRequest(Guid SourceId);
/// <summary>One enrolled training sample feeding a voiceprint: the recording/speaker it came from, and
/// the start (ms) of that speaker's first segment so the UI can play a sample to identify them.</summary>
public record ProfileContributionDto(
    Guid Id, Guid RecordingId, string RecordingName, string SpeakerLabel, long StartMs, DateTimeOffset CreatedAt);
/// <summary>A voiceprint with its training provenance and how many recording-speakers it currently labels.</summary>
public record SpeakerProfileDetailDto(
    Guid Id, string Name, int SampleCount, int IdentifiedCount, IReadOnlyList<ProfileContributionDto> Contributions);
/// <summary>Per-recording speaker: its label, shown name, the matched voiceprint (if any), whether
/// the name was set automatically, and whether it's flagged "Multiple Speakers" (overlapping speech).</summary>
public record SpeakerInfoDto(
    string Label, string DisplayName, Guid? ProfileId, bool IdentifiedAuto, bool IsMultiSpeaker = false);
public record RenameRecordingRequest(string? Name);
/// <summary>Diarization speaker-count hints. Either bound may be null (= no bound / auto).</summary>
public record SpeakerHints(int? Min, int? Max);
/// <summary>Re-transcribe options. <see cref="Speakers"/> is tri-state: omit/null = keep the recording's
/// existing hints; present = set them (an object with null Min/Max means "back to automatic").</summary>
public record RetranscribeRequest(string? Model, SpeakerHints? Speakers = null);
/// <summary>Edit a segment's text. <see cref="Text"/> is tri-state: a value sets the revision (the original
/// is preserved); null = reset to the model's original (clears the revision); "" = a deliberately blank
/// revision.</summary>
public record UpdateSegmentRequest(string? Text);
/// <summary>Delete a set of segments from the current transcription in one call (survivors are renumbered
/// once). Ids not on the caller's recording are ignored.</summary>
public record DeleteSegmentsRequest(IReadOnlyList<Guid> Ids);
/// <summary>Translate a set of segments into <see cref="Language"/> (BCP-47; null = the caller's native
/// language) in one batched LLM call. Ids not on the caller's recording are ignored.</summary>
public record TranslateSegmentsRequest(IReadOnlyList<Guid> Ids, string? Language = null);

// ---- Languages & profile (localization) ----
/// <summary>A supported language: BCP-47 <paramref name="Code"/>, its name in English and in its own
/// script, and whether it is written right-to-left.</summary>
public record LanguageDto(string Code, string EnglishName, string NativeName, bool Rtl);

/// <summary>The signed-in user's editable profile: display name + language preferences (BCP-47, or null
/// = not set / follow the browser). Email is read-only. <paramref name="GoogleConnected"/> is true when the
/// account is linked to a Google identity (used by the Preferences "Google account" section).</summary>
public record UserProfileDto(
    string Email, string? FullName, string? NativeLanguage, string? UiLanguage, bool GoogleConnected = false,
    bool GoogleCalendar = false);

/// <summary>Self-service profile update. Each field is trimmed; blank clears it. Language codes must be
/// in the supported set (else 400).</summary>
public record UpdateUserProfileRequest(string? FullName, string? NativeLanguage, string? UiLanguage);

/// <summary>Translate a transcript (or one segment) into <see cref="Language"/> (BCP-47). When null, the
/// caller's saved native language is used; 400 if neither is set.</summary>
public record TranslateRequest(string? Language = null);

// ---- User settings (per-user summarisation config) ----
/// <summary>Settings returned to the client. The API key is never exposed — only whether one is set.
/// The Default* fields are the server-wide values, shown as placeholders so the user can see what
/// applies when they leave a field blank (without those defaults being persisted as their own).</summary>
public record UserSettingsDto(
    string? ApiBase, string? Model, bool HasApiKey,
    string? DefaultApiBase, string? DefaultModel, bool ServerHasApiKey,
    int? ContextWindow, int DefaultContextWindow,
    bool ToolsEnabled, bool DefaultToolsEnabled, IReadOnlyList<ChatToolDto> Tools,
    bool ReasoningEnabled, string ReasoningEffort, bool DefaultReasoningEnabled, string DefaultReasoningEffort);

/// <summary>A chat tool's state for the settings panel: whether it is on for this user
/// (<paramref name="Enabled"/>) and its server-side default.</summary>
public record ChatToolDto(string Name, string Title, string Description, bool Enabled, bool DefaultEnabled);

/// <summary>Update request. ApiKey is tri-state: null = leave unchanged, "" = clear, value = set.
/// ContextWindow: null/&lt;=0 clears the per-user override (falls back to the server default).
/// ToolsEnabled: null leaves the master switch unchanged; a value sets the per-user override.
/// ToolOverrides: null leaves the per-tool overrides unchanged; a map (possibly empty) replaces them.
/// ReasoningEnabled: null leaves the override unchanged; a value sets it. ReasoningEffort: blank clears
/// the per-user override (falls back to the server default), a value sets it.</summary>
public record UpdateUserSettingsRequest(
    string? ApiBase, string? Model, string? ApiKey, int? ContextWindow = null,
    bool? ToolsEnabled = null, IReadOnlyDictionary<string, bool>? ToolOverrides = null,
    bool? ReasoningEnabled = null, string? ReasoningEffort = null);

// ---- MCP access tokens ----
/// <summary>A stored MCP token, listed in Preferences. The secret is never returned — only a short display
/// <paramref name="Prefix"/> and usage timestamps.</summary>
public record McpTokenDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>The response to generating a token: the plaintext <paramref name="Token"/> is returned exactly
/// once (never retrievable again) so the user can paste it into Claude's MCP config.</summary>
public record McpTokenCreatedDto(Guid Id, string Name, string Prefix, string Token);

/// <summary>Request to mint a new MCP token with a user-supplied label.</summary>
public record CreateMcpTokenRequest(string? Name);

// ---- Chat ----
public record ChatTurnDto(string Role, string Content);

/// <summary>The context a chat turn (or a saved conversation) runs against. <paramref name="SearchAllMeetings"/>
/// is the "All meetings" mode: no transcripts are pre-loaded and the assistant is told to answer by searching
/// the user's whole library on demand.</summary>
public record SavedChatContextDto(
    IReadOnlyList<Guid> RecordingIds, string? AttachmentName, string? AttachmentText,
    bool IncludeAttachments = false, bool SearchAllMeetings = false);

/// <summary>A streaming chat request: the selected context + the full conversation so far.</summary>
public record ChatStreamRequest(
    IReadOnlyList<Guid> RecordingIds,
    string? AttachmentName,
    string? AttachmentText,
    IReadOnlyList<ChatTurnDto> Messages,
    bool IncludeAttachments = false,
    bool SearchAllMeetings = false);

/// <summary>Extracted attachment text returned to the client (held and resent with each turn).</summary>
public record ChatAttachmentDto(string Name, int Chars, string Text);

public record ChatConversationSummaryDto(Guid Id, string Title, DateTimeOffset UpdatedAt);

public record ChatConversationDto(
    Guid Id, string Title, IReadOnlyList<ChatTurnDto> Messages, SavedChatContextDto Context,
    DateTimeOffset UpdatedAt);

public record SaveChatConversationRequest(
    IReadOnlyList<ChatTurnDto> Messages, SavedChatContextDto Context);

public record SaveChatConversationResult(Guid Id, string Title);
