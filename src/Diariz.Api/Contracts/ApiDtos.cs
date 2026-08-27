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

/// <summary>Desktop app -> API: swap a one-time diariz:// code for an access token, proving the app
/// holds the PKCE verifier whose S256 challenge was bound to the code.</summary>
public record DesktopExchangeRequest(string Code, string Verifier);

// ---- Admin user management ----
/// <summary><see cref="PictureUrl"/> is the linked Google account's avatar, so the admin console can show a
/// face rather than initials. It is the same column the account menu already reads; it was simply never
/// projected here.</summary>
public record AdminUserDto(
    Guid Id, string Email, string? FullName, string AccountType, UserStatus Status, bool IsEnabled,
    long QuotaBytes, long UsedBytes, bool HasGoogle = false, string? PictureUrl = null);
public record AddUserRequest(string Email, string? FullName = null);
public record SetQuotaRequest(long QuotaBytes);
public record SetRoleRequest(string Role);
public record SetEnabledRequest(bool IsEnabled);
/// <summary>Result of granting access: whether the link was emailed, and (on the no-SMTP fallback)
/// the setup URL for the admin to share manually.</summary>
public record GrantResultDto(bool Emailed, string? SetupUrl);

// ---- Platform settings & storage quotas ----
/// <summary>Platform-wide storage-quota defaults (bytes) and audio-retention policy, edited by the Platform
/// Administrator. <see cref="AudioDeletionTimeOfDay"/> is the server-local time the nightly job runs.</summary>
public record PlatformSettingsDto(
    long StarterQuotaBytes, long MaxQuotaBytes, MinutesGenerationMode MinutesGenerationMode,
    bool AutoDeleteAudioEnabled, int AudioRetentionDays, TimeOnly AudioDeletionTimeOfDay,
    bool ApiAccessEnabled, int LlmTimeoutSeconds,
    bool McpAccessEnabled, bool WebhooksEnabled,
    bool LlmUsageLoggingEnabled, int LlmUsageRetentionDays, bool LlmStreamUsageEnabled,
    double IdentificationThreshold, double IdentificationConfirmBand, double IdentificationMargin,
    int IdentificationMinSpeechMs);
public record UpdatePlatformSettingsRequest(
    long StarterQuotaBytes, long MaxQuotaBytes,
    MinutesGenerationMode MinutesGenerationMode = MinutesGenerationMode.SingleCall,
    bool AutoDeleteAudioEnabled = false,
    int AudioRetentionDays = PlatformSettings.DefaultAudioRetentionDays,
    TimeOnly AudioDeletionTimeOfDay = default,
    bool ApiAccessEnabled = false,
    int LlmTimeoutSeconds = PlatformSettings.DefaultLlmTimeoutSeconds,
    bool McpAccessEnabled = true,
    bool WebhooksEnabled = false,
    bool LlmUsageLoggingEnabled = true,
    int LlmUsageRetentionDays = PlatformSettings.DefaultLlmUsageRetentionDays,
    bool LlmStreamUsageEnabled = true,
    double IdentificationThreshold = PlatformSettings.DefaultIdentificationThreshold,
    double IdentificationConfirmBand = PlatformSettings.DefaultIdentificationConfirmBand,
    double IdentificationMargin = PlatformSettings.DefaultIdentificationMargin,
    int IdentificationMinSpeechMs = PlatformSettings.DefaultIdentificationMinSpeechMs);

/// <summary>A voice Diariz thinks it recognises but is not confident enough to name unasked.
///
/// <para>Carries everything needed to judge it without opening the recording: who it suspects, how far apart
/// the two voiceprints are, and how much that speaker actually says - a near match on four seconds of speech
/// deserves more scepticism than the same distance on four minutes.</para></summary>
public record SpeakerSuggestionDto(
    Guid SpeakerId,
    Guid RecordingId,
    string RecordingName,
    string SpeakerLabel,
    Guid PersonId,
    string PersonName,
    /// <summary>Cosine distance, lower is closer.</summary>
    double Distance,
    long SpeechMs,
    DateTimeOffset SuggestedAt);

/// <summary>What a re-scan did, or would do in a dry run.</summary>
public record RescanRunResult(int Scanned, int Applied, int Suggested);
/// <summary>Result of a manual "run the audio-retention pass now" trigger: how many recordings had audio deleted.</summary>
public record AudioRetentionRunResult(int Deleted);
/// <summary>Result of a manual "backfill tags now" trigger: how many extraction jobs were ENQUEUED (the
/// tags worker processes them asynchronously; this is not a completion count).</summary>
public record TagBackfillRunResult(int Enqueued);
/// <summary>One aggregated tag-cloud entry across the caller's library: display text, how many recordings
/// carry it, the summed per-recording weight (frequency × salience — drives the cloud's font size), and the
/// carrying recording ids (so the client filters its cached list without a second request).</summary>
public record TagCloudEntryDto(string Tag, int Count, double Weight, IReadOnlyList<Guid> RecordingIds);
/// <summary>The signed-in user's storage usage vs their quota (bytes), plus the total wall-clock time
/// spent transcribing all their recordings (ms, summed across every transcription version).</summary>
public record StorageUsageDto(long UsedBytes, long QuotaBytes, long TotalTranscriptionMs = 0);

// ---- Sections ----
/// <summary>A user section. <c>ParentId</c> is null for a top-level section, or the id of the parent
/// section for a sub-section (one level of nesting). <c>Position</c> is the manual order among siblings.</summary>
public record SectionDto(Guid Id, string Name, Guid? ParentId, int Position);
public record CreateSectionRequest(string Name, Guid? ParentId = null, Guid? RoomId = null);
public record RenameSectionRequest(string Name);
public record MoveRecordingRequest(Guid? SectionId, Guid? RoomId = null);
/// <summary>Move several recordings into one folder in a single call (the paste half of cut/paste). Unlike
/// <see cref="ReorderRecordingsRequest"/>, which sets an explicit 0..n-1 order, this APPENDS: the listed
/// recordings land after whatever is already in the target folder, keeping the order they are listed in.
/// <c>SectionId</c> null = ungroup. <c>RoomId</c> null = the caller's personal room.</summary>
public record MoveRecordingsRequest(IReadOnlyList<Guid> Ids, Guid? SectionId, Guid? RoomId = null);
/// <summary>Reorder/move: set each listed recording's section and position (0..n-1) within a room in one call.
/// <c>RoomId</c> null = the caller's personal room.</summary>
public record ReorderRecordingsRequest(Guid? SectionId, IReadOnlyList<Guid> OrderedIds, Guid? RoomId = null);
/// <summary>Reorder/reparent sections: set each listed section's parent and position (0..n-1) in one
/// call. <c>ParentId</c> null = top level. Reparenting under a section that itself has a parent is rejected.</summary>
public record ReorderSectionsRequest(Guid? ParentId, IReadOnlyList<Guid> OrderedIds, Guid? RoomId = null);

// ---- Section (folder) page ----
/// <summary>High-level folder stats aggregated across a section and its child sections.</summary>
public record SectionStatsDto(
    int TranscriptCount, long TotalDurationMs,
    DateTimeOffset? FirstRecordingAt, DateTimeOffset? LastRecordingAt);

/// <summary>The folder-level LLM summary (roll-up of the included recordings' summaries), with generation
/// state so a client that missed the SignalR event still recovers.</summary>
public record FolderSummaryDto(
    string Model, string Text, DateTimeOffset CreatedAt, bool IsUserEdited,
    SectionGenerationStatus Status, string? Error);

/// <summary>The folder-level LLM minutes (the included recordings' minutes reshaped through a template).</summary>
public record FolderMinutesDto(
    string Model, string Text, DateTimeOffset CreatedAt, bool IsUserEdited,
    Guid? MeetingTypeId, SectionGenerationStatus Status, string? Error);

/// <summary><c>RoomId</c> is the section's ACTUAL room - the web uses it to resolve permissions (e.g.
/// ManageContents for folder-direct attachments) against the folder it is really looking at, rather than
/// whatever room the URL happens to name (the room-less legacy <c>/sections/{id}</c> deep-link carries none,
/// and falls back to the caller's personal room, which is not necessarily where this folder lives).</summary>
public record SectionDetailDto(
    Guid Id, string Name, Guid? ParentId, Guid RoomId,
    SectionStatsDto Stats, FolderSummaryDto? Summary, FolderMinutesDto? Minutes, Guid? MeetingTypeId);

/// <summary>One note aggregated for the folder Notes tab, carrying its source recording's display name and
/// owner (<c>RecordedByUserId</c> = the recording's <c>UserId</c>, mirroring the <c>OwnsAsync</c> gate on
/// <see cref="MeetingNotesController"/>'s mutating routes) so the web can hide edit/delete for rows it isn't
/// allowed to mutate - a room co-viewer's rows come from other people's recordings.</summary>
public record SectionNoteListItemDto(
    Guid Id, Guid RecordingId, string RecordingName, string Text,
    long? CapturedAtMs, int Ordinal, DateTimeOffset CreatedAt, Guid RecordedByUserId);

/// <summary>One attachment aggregated for the folder Attachments tab, carrying its source recording name and
/// owner (see <see cref="SectionNoteListItemDto.RecordedByUserId"/> - same rationale, mirrors
/// <see cref="AttachmentsController"/>'s owner-only gate).</summary>
public record SectionAttachmentListItemDto(
    Guid Id, Guid RecordingId, string RecordingName, AttachmentKind Kind,
    string Name, string? ContentType, long SizeBytes, string? Url, int Ordinal, Guid RecordedByUserId);

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
    string? CalendarColor = null,
    /// <summary>The chosen meeting type driving the minutes template, or null for the General default.</summary>
    Guid? MeetingTypeId = null,
    /// <summary>Wall-clock instant capture began. Null for uploaded files and rows predating the field - show
    /// <see cref="CreatedAt"/> then. This, not <see cref="CreatedAt"/>, is when the meeting happened.</summary>
    DateTimeOffset? StartedAt = null,
    /// <summary>Wall-clock instant capture stopped. Null when unknown; fall back to
    /// <see cref="StartedAt"/> + <see cref="DurationMs"/>, which excludes any paused time.</summary>
    DateTimeOffset? EndedAt = null);

/// <summary>A recording's persisted link to a Google Calendar event (the stored snapshot). The rich invite
/// details are fetched live via <c>GET /api/calendar/events/{eventId}</c>. <see cref="CalendarId"/> targets
/// the calendar the event lives on (primary or a secondary/shared/subscribed one).</summary>
public record CalendarLinkDto(
    string EventId, string CalendarId, string? Summary, DateTimeOffset Start, DateTimeOffset End,
    string? HtmlLink, bool LinkedManually, string? Color = null);

/// <summary>One earlier recording of the same recurring meeting. The times are the <b>occurrence's</b>, taken
/// from the link snapshot rather than the recording, so the list reads as a calendar history ("Tue 3 Jun")
/// rather than as upload timestamps.</summary>
public record SeriesRecordingDto(Guid Id, string Title, string? Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>Link a recording to a calendar event. <paramref name="Manual"/> = the user picked it by hand
/// (vs. the auto-saved best time-overlap match). <paramref name="CalendarId"/> is optional - when omitted the
/// server finds which calendar the event is on.</summary>
public record LinkCalendarRequest(string EventId, bool Manual = false, string? CalendarId = null);

/// <summary>An external iCalendar (<c>.ics</c>) feed the user has subscribed to. <see cref="LastError"/> is the
/// last fetch failure (null when healthy) so the UI can flag a broken feed.</summary>
public record IcsFeedDto(
    Guid Id, string Name, string Url, string? Color, bool Enabled,
    DateTimeOffset? LastFetchedAt, string? LastError);

/// <summary>Create or update an external <c>.ics</c> feed. <paramref name="Enabled"/> defaults to on.</summary>
public record IcsFeedRequest(string Name, string Url, string? Color = null, bool Enabled = true);

// ---- Desktop Outlook calendar mirror ----

/// <summary>One machine that has connected its local desktop Outlook calendar. <paramref name="LastSyncedAt"/>
/// / <paramref name="LastError"/> / <paramref name="EventCount"/> are the health triple shown in Preferences,
/// mirroring an <c>.ics</c> feed's.</summary>
public record OutlookSourceDto(
    Guid Id, string DeviceId, string? DeviceName, string? MailboxName, string DisplayName, string? Color,
    bool Enabled, int PastDays, int FutureDays, bool SkipPrivate, bool IncludeBody,
    DateTimeOffset? LastSyncedAt, string? LastError, int EventCount);

/// <summary>Update a connected device. Every field is optional - omit one to leave it unchanged.</summary>
public record OutlookSourceRequest(
    string? DisplayName = null, string? Color = null, bool? Enabled = null,
    int? PastDays = null, int? FutureDays = null, bool? SkipPrivate = null, bool? IncludeBody = null,
    /// <summary>The last failure the desktop hit, so a connector broken on one machine is visible from any
    /// other. Empty string clears it.</summary>
    string? LastError = null);

/// <summary>Which machine a push came from. The device id is minted once by the desktop app and is what makes
/// the source row per-device rather than per-user.</summary>
public record OutlookDeviceDto(string DeviceId, string? DeviceName = null, string? MailboxName = null, string? TimeZone = null);

/// <summary>One attendee on a pushed appointment. <paramref name="Response"/> uses Google's vocabulary
/// (accepted/declined/tentative/needsAction), mapped on the desktop.</summary>
public record OutlookAttendeeInput(string? Name = null, string? Email = null, string? Response = null, bool Optional = false);

/// <summary>One flattened occurrence as the desktop reports it. All-day entries carry
/// <paramref name="StartDate"/>/<paramref name="EndDate"/> as <b>local</b> <c>yyyy-MM-dd</c> strings; timed
/// ones carry absolute instants in <paramref name="Start"/>/<paramref name="End"/>.</summary>
public record OutlookEventInput(
    string Uid,
    string? Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay = false,
    string? StartDate = null,
    string? EndDate = null,
    string? WindowsTimeZoneId = null,
    string? Location = null,
    string? OnlineMeetingUrl = null,
    string? BodyText = null,
    string? Categories = null,
    int Sensitivity = 0,
    int BusyStatus = 2,
    bool IsRecurring = false,
    string? OrganizerName = null,
    string? OrganizerEmail = null,
    IReadOnlyList<OutlookAttendeeInput>? Attendees = null,
    DateTimeOffset? LastModified = null);

/// <summary>One page of a sync run. The desktop sends the whole rolling window and the <b>server</b> reconciles
/// (upsert what is present, delete what is absent), because the desktop has no trustworthy view of server state
/// - a restore from backup, a second machine, a reinstall or a disable/re-enable would all silently stale a
/// local mirror, and every one of those produces missing events.</summary>
public record OutlookSyncRequest(
    /// <summary>One id per run, repeated on every page. Rows are stamped with it, and the sweep deletes
    /// in-window rows still carrying an older one.</summary>
    Guid SyncId,
    OutlookDeviceDto Device,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<OutlookEventInput> Events,
    /// <summary>False when the desktop's enumeration threw partway. A partial read <b>never</b> sweeps - this
    /// is the guard against deleting a user's mirrored calendar because one COM call failed.</summary>
    bool Complete = true,
    int PageIndex = 0,
    /// <summary>True on the last page only. The sweep runs then, and only when <paramref name="Complete"/>.</summary>
    bool Final = false);

/// <summary>What one pushed page changed.</summary>
public record OutlookSyncResultDto(
    Guid SourceId, Guid SyncId, int Created, int Updated, int Unchanged, int Deleted, int Skipped,
    int EventCount, DateTimeOffset SyncedAt);

/// <summary>Bulk delete the audio blobs of the listed recordings (keeps their transcripts/metadata).</summary>
public record DeleteAudioRequest(IReadOnlyList<Guid> Ids);

/// <summary>Merge the listed recordings' transcripts (and audio) into the earliest-created one.</summary>
public record MergeRecordingsRequest(IReadOnlyList<Guid> Ids);

public record SegmentDto(
    Guid Id, string Speaker, string SpeakerDisplay, long StartMs, long EndMs,
    string Original, string? Revised = null,
    /// <summary>True when this segment has aligned word timings and can therefore be split. False for
    /// anything transcribed before word timings were kept, for a language with no alignment model, and for
    /// a merged block whose run contained an edited segment.
    ///
    /// <para>The words themselves are deliberately not here: roughly 10k per recording would dominate a
    /// payload that also feeds exports, MCP, webhooks and the n8n node, so they are fetched one segment at
    /// a time. Only the recording-detail endpoint sets this - the prompt builders and export formatters
    /// that also project a <c>SegmentDto</c> offer no splitting, so false is correct for them.</para></summary>
    bool HasWords = false)
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
    CalendarLinkDto? CalendarLink = null,
    /// <summary>The chosen meeting type driving the minutes template, or null for the General default.</summary>
    Guid? MeetingTypeId = null,
    /// <summary>When the owner protected the audio from deletion (null = not protected).</summary>
    DateTimeOffset? AudioProtectedAt = null,
    /// <summary>When the audio blob was deleted (null = still present). Mirrors <see cref="HasAudio"/>.</summary>
    DateTimeOffset? AudioDeletedAt = null,
    /// <summary>Projected date the nightly job will delete this recording's audio (`CreatedAt` + retention days),
    /// or null when auto-delete is off, the recording is protected/ineligible, or the audio is already gone.</summary>
    DateTimeOffset? AudioScheduledDeletionAt = null,
    /// <summary>The spoken language this recording is pinned to (BCP-47), or null when it is auto-detected.</summary>
    string? TranscriptionLanguage = null,
    /// <summary>Who recorded it (the owner) - drives the "Recorded by" line. Null name = a deleted/unknown user.
    /// Nullable purely so it can carry a default: a non-nullable struct with a default value crashes the OpenAPI
    /// schema exporter (same reason the admin surface with its <c>TimeOnly</c> field is excluded); in practice it
    /// is always set to the recorder's id.</summary>
    Guid? RecordedByUserId = null,
    string? RecordedByName = null,
    /// <summary>The rooms this recording is placed in that the caller can see, main room first.</summary>
    IReadOnlyList<RecordingRoomDto>? Rooms = null,
    /// <summary>Wall-clock instant capture began. Null for uploaded files and rows predating the field - show
    /// <see cref="CreatedAt"/> then. This, not <see cref="CreatedAt"/>, is when the meeting happened.</summary>
    DateTimeOffset? StartedAt = null,
    /// <summary>Wall-clock instant capture stopped. Null when unknown; fall back to
    /// <see cref="StartedAt"/> + <see cref="DurationMs"/>, which excludes any paused time.</summary>
    DateTimeOffset? EndedAt = null,
    /// <summary>The tags the user adopted on this recording, in the order they adopted them. These are the
    /// only tags the tag cloud counts.</summary>
    IReadOnlyList<string>? Tags = null,
    /// <summary>Tags the LLM proposed that nobody has accepted or dismissed yet, heaviest first - the hub's
    /// "pick or ignore" hints. Dismissed suggestions are never returned.</summary>
    IReadOnlyList<string>? SuggestedTags = null,
    /// <summary>Whether the caller may add/remove/dismiss this recording's tags - the same
    /// owner-or-<see cref="RoomPermission"/>.<see cref="RoomPermission.EditOthersRecordings"/> rule
    /// <c>GateTagWriteAsync</c> enforces on the write endpoints. Defaults to false so a projection that forgets
    /// to set it fails closed to a read-only popover rather than silently granting edit rights.</summary>
    bool CanEditTags = false);

/// <summary>A room a recording sits in, for the detail Overview. <paramref name="IsMain"/> marks the recorder's
/// personal (home) room - the only room a recording can be deleted from.</summary>
/// <summary>A room the recording is placed in, plus the folder it sits in <em>within that room</em>
/// (null = the room's top level). Per-room because the same recording is filed independently in each room
/// it is shared into - the detail page's folder chips read the entry for the room being viewed.</summary>
public record RecordingRoomDto(Guid Id, string Name, string? Icon, string? Color, bool IsMain, Guid? SectionId);

/// <summary>Toggle a recording's protection from audio deletion (auto and manual).</summary>
public record SetAudioProtectionRequest(bool Protected);

/// <summary>Share a recording from one room into another. Requires <c>ShareOut</c> in <paramref name="FromRoomId"/>
/// and <c>CreateRecording</c> in <paramref name="ToRoomId"/>. The link lands ungrouped in the target for now.</summary>
public record ShareRecordingRequest(Guid FromRoomId, Guid ToRoomId);

// ---- Meeting types (minutes templates) ----
/// <summary>A meeting type (minutes template). <see cref="IsPlatform"/> = a shared, admin-owned type;
/// <see cref="CanEdit"/> = the caller may edit/delete it (owns a Personal type, or is a Platform Admin for a
/// Platform type). A type carries no prompts: <see cref="PrimaryFormulaId"/> is the formula whose template
/// generates the minutes, and <see cref="AdditionalFormulaIds"/> run alongside in the same pipeline.</summary>
public record MeetingTypeDto(
    Guid Id, bool IsPlatform, bool CanEdit, string GroupName, string Title, string Overview,
    string Icon, string Color,
    Guid? PrimaryFormulaId, IReadOnlyList<Guid> AdditionalFormulaIds, bool IsDefault = false);

/// <summary>Create or update a meeting type. <paramref name="IsPlatform"/> requests a shared Platform type
/// (honoured only for Platform Administrators; normal users always get a Personal type).</summary>
public record MeetingTypeRequest(
    string GroupName, string Title, string Overview, string Icon, string Color,
    Guid? PrimaryFormulaId, IReadOnlyList<Guid>? AdditionalFormulaIds, bool IsPlatform = false);

/// <summary>Apply a meeting type to a recording (re-runs the minutes). Null = the General default.</summary>
public record ApplyMeetingTypeRequest(Guid? MeetingTypeId);

/// <summary>Adopt or dismiss one tag on a recording. The text is normalised server-side (whitespace becomes
/// hyphens, hyphens trimmed) and matched case-insensitively against the recording's existing tags.</summary>
public record SetRecordingTagRequest(string Tag);

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
/// <summary>Overwrite a Markdown attachment's content in place (from the in-app TipTap editor).</summary>
public record UpdateAttachmentContentRequest(string Content);

// ---- Action items (extracted from a transcript; user-editable) ----
// Completed/CompletedAt default so export/chat projections that don't track completion stay unchanged;
// the detail + actions-list projections pass the real values.
public record RecordingActionDto(
    Guid Id, string Text, string Actor, string Deadline, int Ordinal,
    bool Completed = false, DateTimeOffset? CompletedAt = null);
public record CreateRecordingActionRequest(string? Text, string? Actor, string? Deadline);
public record UpdateRecordingActionRequest(string? Text, string? Actor, string? Deadline);

/// <summary>One line of the user's own meeting notes. <paramref name="CapturedAtMs"/> is the offset into
/// the recording clock (null = pre-meeting/post-hoc); immutable after capture.</summary>
public record MeetingNoteDto(Guid Id, string Text, long? CapturedAtMs, int Ordinal, DateTimeOffset CreatedAt);

/// <summary>A screen capture taken during a recording. Bytes are fetched separately from the content and
/// thumb endpoints; this carries only what the list and the transcript row need.</summary>
public record ScreenshotDto(
    Guid Id, long CapturedAtMs, int Width, int Height, long SizeBytes, int Ordinal, DateTimeOffset CreatedAt);

/// <summary>Text read off a capture by an OCR model.
///
/// <para><paramref name="Model"/> is returned rather than kept server-side because it belongs in the
/// provenance line wherever this text is shown. <paramref name="Cached"/> distinguishes a stored result
/// from a fresh extraction, so a caller can offer to re-run rather than silently accepting an old one.</para>
///
/// <para><b>Treat the text as machine-extracted and unverified.</b> Every model measured against a dense
/// capture produced silent errors - misread digits, whole dropped regions, and in one case an entirely
/// invented column of plausible-looking scores.</para></summary>
public record ScreenshotOcrDto(string Text, string Model, int Chars, bool Cached, DateTimeOffset GeneratedAt);

/// <summary>Bulk-append request: the live panel attaches all its lines after upload; single adds send one.</summary>
public record CreateMeetingNotesRequest(IReadOnlyList<CreateMeetingNoteLine> Lines);
public record CreateMeetingNoteLine(string Text, long? CapturedAtMs = null);

public record UpdateMeetingNoteRequest(string Text);

/// <summary>An action across the user's whole library, carrying its source recording (id + display name)
/// so the Actions tab can link back to the transcript. <c>RecordedByUserId</c> is the recording's owner
/// (see <see cref="SectionNoteListItemDto.RecordedByUserId"/> - same rationale, mirrors
/// <see cref="RecordingActionsController"/>'s owner-only gate) so the folder page's aggregated Actions tab can
/// hide edit/delete/complete for rows belonging to someone else's recording.</summary>
public record ActionListItemDto(
    Guid Id, Guid RecordingId, string RecordingName, string Text, string Actor, string Deadline,
    int Ordinal, bool Completed, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt, Guid RecordedByUserId);
/// <summary>Mark a set of actions complete (or not). Ids not owned by the caller are ignored.</summary>
public record CompleteActionsRequest(IReadOnlyList<Guid> Ids, bool Completed);

// ---- People directory (and their optional voiceprints) ----

/// <summary>Someone who appears in meetings. <paramref name="HasVoiceprint"/> is false for the ordinary case
/// of a person added by hand, or one who opted out and had theirs erased.
///
/// <para><paramref name="CanManageBiometrics"/> is the <b>server's</b> answer to whether the caller may opt
/// this person out or erase their voiceprint (<c>ManagePeople</c>, or the person is the caller). Clients must
/// render those controls from this flag rather than recomputing the rule, or the two drift the first time
/// either is edited.</para></summary>
public record PersonDto(
    Guid Id, string Name, string? Title, string? CompanyName, string? Email, string? Phone,
    bool IsInternal, bool VoiceprintOptOut, bool HasVoiceprint, int SampleCount,
    Guid? LinkedUserId, bool IsSelf, bool CanManageBiometrics,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>One voice sample feeding a person's voiceprint: the recording/speaker it came from, and the
/// start (ms) of that speaker's first segment so the UI can play a sample to identify them.</summary>
public record VoiceSampleDto(
    Guid Id, Guid RecordingId, string RecordingName, string SpeakerLabel, long StartMs, DateTimeOffset CreatedAt,
    /// <summary>Total ms of audio selected, or the speaker's whole span when nothing is selected.</summary>
    long SelectedMs = 0,
    /// <summary>Ms the last embedding actually consumed, or null while a recompute is queued. Less than
    /// <paramref name="SelectedMs"/> when the worker's cap truncated the selection - the UI states both
    /// rather than implying the whole selection was used.</summary>
    int? UsedMs = null,
    /// <summary>The contributing speaker's audio was re-attributed, so this snapshot no longer describes
    /// it. <b>Derived</b> by joining to the speaker's <c>EmbeddingStale</c>, never stored twice.</summary>
    bool Stale = false,
    /// <summary>A recompute is queued and has not reported back.</summary>
    bool Pending = false,
    /// <summary>A human has listened and vouched that this recording really is this person. Independent of
    /// whether it trains: one asks who it is, the other whether the audio is worth learning from.</summary>
    bool Confirmed = false,
    /// <summary>The last recompute failed. The sample keeps the vector and the duration it already had - a
    /// failed re-embed must not destroy a working voiceprint - so without this the row would be
    /// indistinguishable from one that simply never ran.</summary>
    bool RecomputeFailed = false,
    /// <summary>The spans of the recording's audio this sample trains on. <b>Empty means the whole
    /// speaker</b>, matching the column's null. The client needs these to know which segments to show as
    /// selected; there are a handful per sample, not thousands, so unlike segment words they ride along on
    /// the person payload.</summary>
    IReadOnlyList<VoiceprintSpan>? Spans = null);

/// <summary>Replace the spans of audio that train one voice sample. An <b>empty list</b> means the whole
/// speaker, which is what every sample does by default.</summary>
public record SetVoiceSampleSpansRequest(IReadOnlyList<VoiceprintSpan> Spans);

/// <summary>A person with their voiceprint's training provenance and how many recording-speakers they
/// currently label.</summary>
public record PersonDetailDto(PersonDto Person, int IdentifiedCount, IReadOnlyList<VoiceSampleDto> Samples);

/// <summary>One speaker attributed to a person, whether or not it trains their voiceprint.
///
/// <para>Strictly larger a set than <see cref="PersonDetailDto.Samples"/>: automatic identification links a
/// speaker without ever creating a voice sample, which is why a list built from samples alone read as
/// arbitrary rather than as "the recordings this person appears in".</para>
///
/// <para><paramref name="IsTraining"/> and <paramref name="VoiceSampleId"/> are independent. An excluded
/// sample is not training but still has an id, because re-including it is a toggle rather than a fresh
/// enrolment.</para>
///
/// <para><paramref name="CanAccessRecording"/> is false when the caller neither owns the recording nor holds
/// <c>ManageVoiceprints</c>. The row is still returned - it is part of what the voiceprint learned from -
/// and the client renders it without a transcript or a play button.</para>
///
/// <para><paramref name="StillLinked"/> is false when the speaker behind this sample no longer names this
/// person: unassigned, reassigned to someone else, or marked as overlapping speech. The row is listed anyway,
/// because hiding it is exactly how six of them survived unnoticed on a live instance while still inside a
/// centroid. <paramref name="IsTraining"/> is false for all of them.</para>
///
/// <para><paramref name="CanReassign"/> is narrower than <paramref name="CanAccessRecording"/> on purpose.
/// <c>ManageVoiceprints</c> grants listening to a segment for assessment, not editing someone else's
/// transcript, and <c>AssignSpeaker</c> requires ownership regardless - so offering the control without it
/// would produce a button that always fails.</para></summary>
public record PersonAttributionDto(
    Guid SpeakerId,
    Guid RecordingId,
    string RecordingName,
    string SpeakerLabel,
    string LinkedBy,
    bool IsTraining,
    Guid? VoiceSampleId,
    long SpeechMs,
    bool CanAccessRecording,
    bool StillLinked,
    bool CanReassign,
    /// <summary>False once the recording's audio has been deleted. The transcript survives, so the row still
    /// lists what was said - but there is nothing left to play, and offering the control anyway produced a
    /// button that silently did nothing. Audio behind a live sample is exempt from the retention sweep from
    /// 0.257.0, so this only stays true for recordings already swept, or deleted by hand.</summary>
    bool AudioAvailable = true);

/// <summary>How well one enrolled sample resembles the rest of a person's training set.
///
/// <para>Two distances, because they answer different questions and disagree in the case that matters most: a
/// sample can sit right next to one companion while that pair together sits well away from the person's
/// centre of mass.</para></summary>
public record SampleDiagnosisDto(
    Guid VoiceSampleId,
    Guid SpeakerId,
    Guid RecordingId,
    string RecordingName,
    string SpeakerLabel,
    /// <summary>Cosine distance to the closest other sample - "does this have company?" Null when there is
    /// no other sample.</summary>
    double? NearestSiblingDistance,
    /// <summary>Cosine distance to the centroid of the person's <b>other</b> samples - "would the rest of
    /// this voiceprint recognise it?" A true leave-one-out. Null when there is no other sample.</summary>
    double? DistanceToOthers,
    /// <summary>"Only", "Core", "Variant", "Alone" or "Impostor".</summary>
    string Verdict,
    bool IsTraining,
    /// <summary>A human has listened and vouched that this recording really is this person. The verdict is
    /// reported unchanged either way - only the review queue shrinks - because hiding it would make a
    /// confirmed outlier indistinguishable from a healthy recording, and somebody who confirmed in haste
    /// needs to be able to see what they signed off.</summary>
    bool Confirmed = false,
    /// <summary>Cosine distance to the closest sample belonging to <b>anyone else</b>. Null in a directory
    /// holding only this person. Reported even when it is not a finding.</summary>
    double? NearestImpostorDistance = null,
    /// <summary>Who that was. Present whenever <paramref name="NearestImpostorDistance"/> is, so the finding
    /// can be acted on with the reassign control already on the row rather than only read.</summary>
    Guid? NearestImpostorPersonId = null,
    string? NearestImpostorName = null);

/// <summary>A person's training set, and how coherent it is.
///
/// <para><paramref name="WidestPair"/> is the largest distance between any two of their samples - one number
/// for "how scattered is this person", which is what the directory ranking sorts on. Null when there is
/// nothing to compare.</para></summary>
public record VoiceprintDiagnosticsDto(
    IReadOnlyList<SampleDiagnosisDto> Samples, int AloneCount, double? WidestPair);

/// <summary>One person's line in the voiceprint-health ranking: how many of their samples resemble nothing
/// else, and how far apart their two most distant samples are.</summary>
public record PersonDiagnosticsSummaryDto(
    Guid PersonId, string Name, int SampleCount, int AloneCount, double? WidestPair,
    /// <summary>How many of this person's samples sit closer to somebody else than to any of their own.
    /// Ranked above <paramref name="AloneCount"/>: "this is somebody else" is a different order of problem
    /// from "this sounds unlike the rest", and the one that becomes a confident false match if clustered.
    /// </summary>
    int ImpostorCount = 0);

/// <summary>Whether a speaker attributed to a person should train their voiceprint.</summary>
public record SetTrainingRequest(bool Training);

/// <summary>Vouch for a recording behind a voiceprint, or take that back. <b>Not</b> the same as
/// <see cref="SetTrainingRequest"/>: that one asks whether the audio is good enough to learn from, this asks
/// whether it is the right person.</summary>
public record SetSampleConfirmedRequest(bool Confirmed);

/// <summary>One segment spoken by an attributed speaker, for choosing which audio trains a voiceprint.
///
/// <para>Deliberately <b>only that speaker's</b> segments, never the recording's transcript. An assessor may
/// be granted this person's speech in a recording they do not own; handing over everybody else's words in the
/// same response would undo exactly the narrowing the clip endpoint enforces.</para></summary>
public record AttributionSegmentDto(Guid Id, long StartMs, long EndMs, string Text);

/// <summary>Create a person. Everything but the name is optional, and supplying
/// <paramref name="RecordingId"/> + <paramref name="Label"/> enrols a voiceprint in the same call - which is
/// what the "new person" affordance on a transcript does.</summary>
public record CreatePersonRequest(
    string Name, string? Title, string? CompanyName, string? Email, string? Phone,
    bool? IsInternal, bool? VoiceprintOptOut, Guid? RecordingId, string? Label);

/// <summary>Edit a person. Every field is nullable and <b>null means "not supplied"</b>, not "clear it" - so
/// a client can send only what it changed.</summary>
public record UpdatePersonRequest(
    string? Name, string? Title, string? CompanyName, string? Email, string? Phone,
    bool? IsInternal, bool? VoiceprintOptOut);

public record EnrolVoiceprintRequest(Guid RecordingId, string Label);
public record MergePeopleRequest(Guid SourceId);

/// <summary>People who look like the same human. <paramref name="Reason"/> is <c>email</c> or <c>name</c>.
/// Reported only - merging is always a person's decision, because it cannot be undone.</summary>
public record PersonDuplicateGroupDto(string Reason, IReadOnlyList<PersonDto> People);

public record AssignSpeakerRequest(Guid? PersonId);
/// <summary>Whether this person should be voice-printed. Turning it on erases any voiceprint they have.</summary>
public record SetVoiceprintOptOutRequest(bool OptOut);
/// <summary>Per-recording speaker: its label, shown name, the matched voiceprint (if any), whether
/// the name was set automatically, and whether it's flagged "Multiple Speakers" (overlapping speech).</summary>
/// <summary>Per-recording speaker: its label, shown name, the person it was identified as (if any), whether
/// the name was set automatically, and whether it is flagged "Multiple Speakers" (overlapping speech).
///
/// <para>The person's details are carried here so a client showing the Speakers panel does not need a second
/// call per speaker. They are all null for an anonymous speaker, and for a "Multiple Speakers" slot, which
/// is by definition not one person.</para></summary>
public record SpeakerInfoDto(
    Guid Id,
    string Label, string DisplayName, Guid? PersonId, bool IdentifiedAuto, bool IsMultiSpeaker = false,
    string? Title = null, string? CompanyName = null, string? Email = null, string? Phone = null,
    bool? IsInternal = null,
    /// <summary>A segment was moved into or out of this speaker, so its stored voiceprint no longer
    /// describes the audio it is attributed to. Nothing recomputes on its own - that needs the worker and
    /// the original audio - so this is what tells the user it is worth doing.</summary>
    bool EmbeddingStale = false,
    /// <summary>A person this speaker may be, close enough to ask about but not to apply. Null unless a
    /// suggestion is pending; when set, the speaker is still anonymous.</summary>
    Guid? SuggestedPersonId = null,
    string? SuggestedPersonName = null,
    /// <summary>Cosine distance to the suggested person, lower is closer.</summary>
    double? SuggestedDistance = null);

/// <summary>Move one segment to a different speaker. A null <see cref="Label"/> asks the API to mint a new
/// speaker for this recording: the interrupting voice often has no diarization slot of its own, and the
/// client must not invent a label into the worker's namespace.</summary>
public record AssignSegmentSpeakerRequest(string? Label);

/// <summary>The speaker a segment now belongs to - including a label the API minted, which the caller
/// could not have known in advance.</summary>
public record SegmentSpeakerDto(string Label, string DisplayName);
public record RenameRecordingRequest(string? Name);
/// <summary>Diarization speaker-count hints. Either bound may be null (= no bound / auto).</summary>
public record SpeakerHints(int? Min, int? Max);
/// <summary>Re-transcribe options. <see cref="Speakers"/> is tri-state: omit/null = keep the recording's
/// existing hints; present = set them (an object with null Min/Max means "back to automatic").</summary>
/// <summary>The spoken language to transcribe in, as a tri-state wrapper (like <see cref="SpeakerHints"/>):
/// a null <see cref="Code"/> means auto-detect, a code pins it. Absent from the request = leave the
/// recording's existing choice alone.</summary>
public record LanguageChoice(string? Code);

public record RetranscribeRequest(string? Model, SpeakerHints? Speakers = null, LanguageChoice? Language = null);
/// <summary>Edit a segment's text. <see cref="Text"/> is tri-state: a value sets the revision (the original
/// is preserved); null = reset to the model's original (clears the revision); "" = a deliberately blank
/// revision.</summary>
public record UpdateSegmentRequest(string? Text);

/// <summary>Split one segment in two, before the word at <see cref="WordIndex"/> (so an index of 1 puts one
/// word on the left). The cut snaps to the stored word timings, and the silence between the two words falls
/// into neither half - which is what a voiceprint trained on the result needs.</summary>
/// <param name="DiscardRevision">Required when the segment carries a manual edit. There is no principled
/// way to divide edited prose at a word index the edit may not even contain, so both halves take their text
/// from the model's original and the caller has to say explicitly that losing the edit is intended.</param>
public record SplitSegmentRequest(int WordIndex, bool DiscardRevision = false);
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
/// <summary>The <c>Person</c> the signed-in account <em>is</em> (<c>Person.LinkedUserId</c>) - the name they
/// appear under in transcripts, and the row that carries their voiceprint. Read-only on the profile: the name
/// follows the account's display name, and the biometric controls live in the people UI.</summary>
public record SelfPersonDto(Guid Id, string Name, bool HasVoiceprint, int SampleCount, bool VoiceprintOptOut);

public record UserProfileDto(
    string Email, string? FullName, string? NativeLanguage, string? UiLanguage, bool GoogleConnected = false,
    bool GoogleCalendar = false,
    string? JobTitle = null, string? CompanyName = null, string? JobDescription = null,
    string? CompanyDescription = null, string? LinkedIn = null,
    /// <summary>Colour theme: "auto" | "light" | "dark".</summary>
    string Theme = "auto",
    /// <summary>Whether the platform has user API access enabled (drives the API card on Preferences →
    /// Integrations).</summary>
    bool ApiAccessEnabled = false,
    /// <summary>Whether the platform has outbound webhooks enabled (drives the Automations card on
    /// Preferences → Integrations).</summary>
    bool WebhooksEnabled = false,
    /// <summary>Whether the platform has personal MCP access enabled (drives the MCP card on Preferences →
    /// Integrations). Defaults true on the server, unlike the other two, so an existing platform keeps
    /// working; it is reported here because the section used to offer its controls whatever an
    /// administrator had set, and the user only learned otherwise when the token request was refused.</summary>
    bool McpAccessEnabled = true,
    /// <summary>The caller's platform permissions, from their group membership. Resolved server-side on every
    /// request: the web must not infer authority from the JWT, which goes stale when membership changes.</summary>
    PermissionsDto? Permissions = null,
    /// <summary>The default spoken language for this user's recordings (BCP-47), or null to auto-detect.</summary>
    string? TranscriptionLanguage = null,
    /// <summary>Who the caller is in the people directory, and whether that person has a voiceprint. Always
    /// sent; nullable only so the positional record stays source-compatible.</summary>
    SelfPersonDto? Person = null);

/// <summary>A user's platform permissions, expanded into booleans so the client never does bit arithmetic.</summary>
public record PermissionsDto(
    bool ManageRooms, bool ManageUsers, bool ManagePlatform, bool ManageFormulas, bool ManagePeople,
    bool ManageVoiceprints);

/// <summary>A room the caller belongs to. <paramref name="Permissions"/> is the caller's effective
/// <see cref="RoomPermission"/> grid as an <b>int</b> bitmask - a [Flags] enum would serialize as "A, B" under
/// the global string-enum converter and break the web's bit arithmetic (see flags-enum-serializes-as-string).</summary>
/// <param name="SectionCount">Folders in this room.</param>
/// <param name="RecordingCount">Recordings placed in this room. A recording shared into several rooms counts
/// in each: the number answers "what will I find in here", not "how many distinct recordings exist".</param>
public record RoomListItemDto(
    Guid Id, string Name, RoomKind Kind, string? Icon, string? Color, bool IsPersonal, int Permissions,
    int SectionCount = 0, int RecordingCount = 0);

/// <summary>Create/rename a shared room. Name is required and unique among shared rooms.</summary>
public record RoomInput(string Name, string? Description, string? Icon, string? Color);

/// <summary>A room member for the Manage Rooms editor: a user or group principal + its permission grid as an
/// <b>int</b> bitmask (a [Flags] enum would serialise as "A, B" and break the web's bit arithmetic).
/// <paramref name="DisplayName"/> is the resolved user/group name (server-side, so a ManageRooms holder who
/// can't list all platform users still sees names, not raw ids); null if the principal no longer exists.</summary>
public record RoomMemberDto(RoomPrincipalType PrincipalType, Guid PrincipalId, int Permissions, string? DisplayName = null);

/// <summary>Upsert a member's permission grid on a room.</summary>
public record RoomMemberInput(RoomPrincipalType PrincipalType, Guid PrincipalId, int Permissions);

/// <summary>A shared room with its membership, for the Manage Rooms editor.</summary>
public record RoomDetailDto(
    Guid Id, string Name, string? Description, string? Icon, string? Color, IReadOnlyList<RoomMemberDto> Members);

/// <summary>Self-service profile update. Each field is trimmed; blank clears it. Language codes must be
/// in the supported set (else 400). <paramref name="Theme"/> is "auto" | "light" | "dark" (unknown -> auto).</summary>
public record UpdateUserProfileRequest(
    string? FullName, string? NativeLanguage, string? UiLanguage,
    string? JobTitle = null, string? CompanyName = null, string? JobDescription = null,
    string? CompanyDescription = null, string? LinkedIn = null, string? Theme = null,
    /// <summary>Default spoken language for new recordings (BCP-47); blank = auto-detect.</summary>
    string? TranscriptionLanguage = null);

/// <summary>Translate a transcript (or one segment) into <see cref="Language"/> (BCP-47). When null, the
/// caller's saved native language is used; 400 if neither is set.</summary>
public record TranslateRequest(string? Language = null);

/// <summary>One of the user's Google calendars for the Preferences picker. <paramref name="Selected"/> is the
/// user's effective Diariz selection (whether its events count toward attribution + the overlay).</summary>
public record CalendarListItemDto(string Id, string? Summary, string? BackgroundColor, bool Primary, bool Selected);

/// <summary>Save which Google calendars to consider for recording attribution + the Calendar overlay.</summary>
public record SaveCalendarSelectionRequest(IReadOnlyList<string> Ids);

// ---- User settings (per-user summarisation config) ----
/// <summary>Settings returned to the client. The API key is never exposed — only whether one is set.
/// The Default* fields are the server-wide values, shown as placeholders so the user can see what
/// applies when they leave a field blank (without those defaults being persisted as their own).</summary>
/// <summary>A user's own preferences.
///
/// The endpoint, key, model, context window, timeout and reasoning fields were removed in 0.221.0: LLM
/// configuration is platform-wide, edited at /admin/llm-models. <paramref name="ContextWindow"/> survives
/// as READ-ONLY - the chat dial still has to show a budget - but it is now the serving model's window
/// rather than anything the user can set.</summary>
public record UserSettingsDto(
    int ContextWindow,
    /// <summary>The model serving chat, for the context dial's label before the first turn reports one.
    /// Read-only: an administrator chooses which models are available at /admin/llm-models.</summary>
    string ChatModel,
    /// <summary>The model this user last chose in the chat picker, or null to follow the platform's chat
    /// routing. Unlike <c>ContextWindow</c> and <c>ChatModel</c> above it, this one IS writable - those two
    /// are derived from it.</summary>
    Guid? ChatModelId,
    bool ToolsEnabled, bool DefaultToolsEnabled, IReadOnlyList<ChatToolDto> Tools,
    // Where a new recording lands in the user's Personal room. The mode serialises as its enum name
    // ("SelectedFolder" etc.) via the global string-enum converter, same as MinutesGenerationMode.
    RecordingPlacementMode PlacementMode, Guid? PlacementSectionId,
    // True when the server has an STT endpoint configured (the dictation server-fallback path is available).
    bool DictationServerAvailable,
    /// <summary>Whether the user has opted in to mirroring a desktop Outlook calendar. Off by default; turning
    /// it off erases every device and its stored meetings.</summary>
    bool OutlookSyncEnabled = false,
    /// <summary>Whether a recording started from a calendar event ends by itself. Off by default; the two
    /// fields below are its conditions and are inert while it is false.</summary>
    bool CalendarAutoStopEnabled = false,
    /// <summary>Minutes to keep recording past the invite's end time.</summary>
    int CalendarAutoStopAfterMinutes = UserSettings.DefaultCalendarAutoStopAfterMinutes,
    /// <summary>Seconds of continuous silence that also ends such a recording.</summary>
    int CalendarSilenceStopSeconds = UserSettings.DefaultCalendarSilenceStopSeconds,
    /// <summary>Whether consecutive same-speaker segments are collapsed automatically once a recording
    /// finishes transcribing. Off by default.</summary>
    bool AutoMergeSpeakerSegments = false);

/// <summary>A chat tool's state for the settings panel: whether it is on for this user
/// (<paramref name="Enabled"/>) and its server-side default.</summary>
public record ChatToolDto(string Name, string Title, string Description, bool Enabled, bool DefaultEnabled);

/// <summary>Update request. The text fields are tri-state: null = leave unchanged, "" = clear the override
/// (falls back to the server default), a value = set. This lets the personal settings tabs (Model Settings,
/// Chat Tools, Recordings) save independently, each PUTting only the fields it owns without wiping the others.
/// So: ApiBase / Model / ReasoningEffort / ApiKey all follow that tri-state.
/// ContextWindow: null leaves it unchanged; &lt;=0 clears the per-user override; a positive value sets it.
/// ToolsEnabled: null leaves the master switch unchanged; a value sets the per-user override.
/// ToolOverrides: null leaves the per-tool overrides unchanged; a map (possibly empty) replaces them.
/// ReasoningEnabled: null leaves the override unchanged; a value sets it.
/// PlacementMode: null leaves the placement unchanged; a value sets it.
/// LlmTimeoutSeconds: null leaves it unchanged; 0 clears the override; a value of 5 or more sets it; 1-4 is
/// rejected rather than coerced.</summary>
public record UpdateUserSettingsRequest(
    bool? ToolsEnabled = null, IReadOnlyDictionary<string, bool>? ToolOverrides = null,
    // PlacementMode: null leaves the placement preference unchanged; a value sets it (and, unless it is
    // SpecificFolder, clears PlacementSectionId).
    RecordingPlacementMode? PlacementMode = null, Guid? PlacementSectionId = null,
    /// <summary>The desktop Outlook sync opt-in. Null leaves it unchanged. Setting it <c>false</c> also
    /// <b>purges</b> every connected device and, by cascade, every meeting mirrored from them - the switch
    /// erases rather than merely stopping future syncs.</summary>
    bool? OutlookSyncEnabled = null,
    /// <summary>Whether a recording started from a calendar event ends by itself. Null leaves it unchanged.</summary>
    bool? CalendarAutoStopEnabled = null,
    /// <summary>Minutes to keep recording past the invite's end. Null leaves it unchanged; a non-positive
    /// value resets to the default rather than persisting a stop-immediately trap.</summary>
    int? CalendarAutoStopAfterMinutes = null,
    /// <summary>Seconds of continuous silence that ends such a recording. Null leaves it unchanged; a
    /// non-positive value resets to the default.</summary>
    int? CalendarSilenceStopSeconds = null,
    /// <summary>Whether transcripts are auto-merged by speaker. Null leaves it unchanged.</summary>
    bool? AutoMergeSpeakerSegments = null,
    /// <summary>The chat model picker's choice. Null leaves it unchanged; <c>Guid.Empty</c> clears the pick
    /// and follows the platform's chat routing; a value sets it. Empty-as-clear mirrors the
    /// "non-positive clears" rule the numeric fields use - a separate boolean would be a second way to say
    /// the same thing.
    ///
    /// Not validated against the offered set here, on purpose: an administrator can un-tick a model at any
    /// time, so a stored pick is always provisional. The read path and every chat turn ignore one that is
    /// not offered, which makes an un-tick reversible - rejecting or clearing it here would destroy the
    /// user's choice permanently.</summary>
    Guid? ChatModelId = null);

// ---- MCP access tokens ----
/// <summary>A stored MCP token, listed in Preferences. The secret is never returned — only a short display
/// <paramref name="Prefix"/> and usage timestamps.</summary>
public record McpTokenDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>The response to generating a token: the plaintext <paramref name="Token"/> is returned exactly
/// once (never retrievable again) so the user can paste it into Claude's MCP config.</summary>
public record McpTokenCreatedDto(Guid Id, string Name, string Prefix, string Token);

/// <summary>Request to mint a new MCP token with a user-supplied label.</summary>
public record CreateMcpTokenRequest(string? Name);

/// <summary>A stored personal REST-API token, listed in Preferences. The secret is never returned - only a
/// short display <paramref name="Prefix"/> and usage timestamps.</summary>
public record ApiTokenDto(
    Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt,
    string Scope, DateTimeOffset? ExpiresAt);

/// <summary>The response to generating an API token: the plaintext <paramref name="Token"/> is returned
/// exactly once (never retrievable again).</summary>
public record ApiTokenCreatedDto(Guid Id, string Name, string Prefix, string Token);

/// <summary>Request to mint a new personal REST-API token with a user-supplied label. <paramref name="ReadOnly"/>
/// requests the <see cref="Diariz.Domain.Entities.ApiTokenScope.ReadOnly"/> scope (default is ReadWrite).
/// <paramref name="ExpiresAt"/> is optional and must be in the future.</summary>
public record CreateApiTokenRequest(string? Name, bool ReadOnly = false, DateTimeOffset? ExpiresAt = null);

// ---- Chat ----
public record ChatTurnDto(string Role, string Content);

/// <summary>One screen capture attached to a chat turn, by reference.
///
/// <para>Ids, never image bytes: the server re-checks that the caller may read the recording, loads the
/// blob itself, and rescales it. That keeps the request body small, keeps a saved conversation storing
/// references rather than megabytes of base64, and means a client cannot supply pixels of its
/// choosing.</para></summary>
public record ChatScreenshotRefDto(Guid RecordingId, Guid ScreenshotId);

/// <summary>One model a chat user may pick.
///
/// Deliberately carries no endpoint and no API key: every signed-in user reads this, unlike
/// <see cref="LlmModelDto"/>, which is administrator-only for exactly that reason.
/// <paramref name="Name"/> is the slug the server sends as <c>model</c>, present so a client can match a
/// streamed usage snapshot back to a label rather than rendering the raw slug.
///
/// <paramref name="SupportsImages"/> is whether this model can be sent screenshots. It gates the chat
/// composer's image attachments, so the client can refuse before sending rather than after the endpoint
/// rejects the turn - or, worse, silently ignores the image and answers anyway.
///
/// <paramref name="SupportsTools"/> is whether the platform will offer this model its chat tools. It is a
/// resolved parameter with an app default of <b>true</b>, so it reads true unless an administrator has
/// turned it off. <paramref name="Description"/> is the administrator's short phrase for the picker, or
/// null where none is set.</summary>
public record ChatModelDto(
    Guid Id, string Label, string Name, int ContextLength, bool IsDefault, bool SupportsImages,
    bool SupportsTools, string? Description);

/// <summary>The context a chat turn (or a saved conversation) runs against. <paramref name="SearchAllMeetings"/>
/// is the "All meetings" mode: no transcripts are pre-loaded and the assistant is told to answer by searching
/// the user's whole library on demand.</summary>
public record SavedChatContextDto(
    IReadOnlyList<Guid> RecordingIds, string? AttachmentName, string? AttachmentText,
    bool IncludeAttachments = false, bool SearchAllMeetings = false, Guid? SectionId = null,
    /// <summary>The model this conversation was using when it was saved, so reopening it puts the user back
    /// on that model. Null for conversations saved before 0.231.0, and for one on the platform default.
    /// Stored, not validated: if the model is no longer offered the client falls back to the default, the
    /// same way a chat turn does.</summary>
    Guid? ModelId = null,
    /// <summary>Screen captures attached to this conversation, so reopening it restores the tray and the
    /// model can still answer follow-ups about them. Null for conversations saved before 0.238.0. Stored,
    /// not validated: a capture deleted in the meantime is dropped on reload rather than erroring - a saved
    /// chat should reopen.</summary>
    IReadOnlyList<ChatScreenshotRefDto>? Screenshots = null);

/// <summary>A streaming chat request: the selected context + the full conversation so far.
/// <paramref name="SectionId"/> (when set) makes the chat about a folder - its roll-up summary, minutes and
/// aggregated actions become the context (and, with <paramref name="IncludeAttachments"/>, the folder's
/// attachments).</summary>
public record ChatStreamRequest(
    IReadOnlyList<Guid> RecordingIds,
    string? AttachmentName,
    string? AttachmentText,
    IReadOnlyList<ChatTurnDto> Messages,
    bool IncludeAttachments = false,
    bool SearchAllMeetings = false,
    Guid? SectionId = null,
    /// <summary>A model from <c>GET /api/chat/models</c> to answer this turn. Null, unknown, or a model the
    /// platform does not offer for chat all mean the same thing: the administrator's chat model answers.
    ///
    /// The request is stateless, so switching model part-way through a conversation needs nothing else -
    /// the full history is resent every turn and reaches the new model as a matter of course.</summary>
    Guid? ModelId = null,
    /// <summary>Screen captures to send with this turn, by reference. Each is re-checked against what the
    /// caller may read, loaded from storage, and rescaled to fit a vision model.
    ///
    /// <para>Requires a model whose <c>images_supported</c> parameter is on: attaching captures to a
    /// text-only model is a 400, not a silent drop. Answering a question about a picture the model never
    /// saw is the failure worth refusing.</para></summary>
    IReadOnlyList<ChatScreenshotRefDto>? Screenshots = null);

/// <summary>Extracted attachment text returned to the client (held and resent with each turn).</summary>
public record ChatAttachmentDto(string Name, int Chars, string Text);

/// <summary>Which existing attachment to read into chat context. Exactly one of <paramref name="RecordingId"/>
/// and <paramref name="SectionId"/> identifies where it hangs, and therefore which access rule applies.</summary>
public record ChatLibraryAttachmentRequest(Guid AttachmentId, Guid? RecordingId = null, Guid? SectionId = null);

/// <summary>The recognised text for one dictation utterance.</summary>
public record ChatTranscriptionDto(string Text);

public record ChatConversationSummaryDto(Guid Id, string Title, DateTimeOffset UpdatedAt);

public record ChatConversationDto(
    Guid Id, string Title, IReadOnlyList<ChatTurnDto> Messages, SavedChatContextDto Context,
    DateTimeOffset UpdatedAt);

public record SaveChatConversationRequest(
    IReadOnlyList<ChatTurnDto> Messages, SavedChatContextDto Context);

public record SaveChatConversationResult(Guid Id, string Title);

// ---- Formulas ----

/// <summary>A saved template + context, as listed for the caller: their own Personal formulas plus every
/// enabled Platform/Diariz formula. <paramref name="Scope"/> serialises as the enum name ("Personal" /
/// "Platform" / "Diariz") via the global string-enum converter. <paramref name="Context"/> is the
/// [Flags] bit value exposed as a plain int - NOT the enum itself, which the global converter would
/// otherwise render as a comma-separated string the web can't do bit arithmetic on (see CLAUDE.md's
/// "Flags enum serializes as string" gotcha).</summary>
public record FormulaDto(
    Guid Id, string Scope, Guid? OwnerUserId, string Name, string? Description,
    Diariz.Api.Services.TemplateContent Content,
    int Context, bool Enabled, bool IsBuiltIn, bool Shared, Guid[] Signals);

/// <summary>A formula shared by another user, for the discovery browser: the formula, the owner's display +
/// avatar (name falls back to email), and whether the caller has already added it.</summary>
public record SharedFormulaDto(FormulaDto Formula, string? OwnerName, string? OwnerPictureUrl, bool AlreadyAdded);

/// <summary>Create request. <paramref name="Scope"/> is parsed to <see cref="FormulaScope"/>;
/// <paramref name="Context"/> is the [Flags] bit value as an int.</summary>
public record CreateFormulaRequest(
    string Scope, string Name, string? Description, Diariz.Api.Services.TemplateContent Content,
    int Context, bool Shared = false, Guid[]? Signals = null);

/// <summary>Partial update: null leaves a field unchanged, mirroring the tri-state pattern used by
/// <see cref="UpdateUserSettingsRequest"/> (a value replaces it - none of these fields need a separate
/// "clear" state, unlike the optional overrides in user settings). <paramref name="Signals"/> follows the
/// same null-means-unchanged rule; a non-null array (including empty) reconciles the attached Workflow
/// Signals to exactly that set.</summary>
public record UpdateFormulaRequest(
    string? Name, string? Description, Diariz.Api.Services.TemplateContent? Content, int? Context,
    bool? Shared = null, Guid[]? Signals = null);

public record SetFormulaEnabledRequest(bool Enabled);

/// <summary>List/summary shape for a recording's formula results. The generated Markdown <c>Text</c> is
/// deliberately NOT included here - it's fetched separately via a dedicated text endpoint so listing a
/// recording's results stays cheap. <c>Status</c> is a <c>FormulaRunStatus</c> name (Generating/Ready/Failed)
/// so the UI can show a pending/failed row from an async run; <c>Error</c> carries the failure reason.</summary>
public record FormulaResultDto(
    Guid Id, Guid RecordingId, string Name, Guid? CreatedByUserId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, FormulaResultOriginDto Origin,
    string Status, string? Error);

/// <summary>Where a formula result came from, for the runs-list icon. Kind: "diariz" | "platform" |
/// "personal". Diariz/Platform are "official" (no person - the UI shows the Diariz logo); "personal" carries
/// the person's display + picture for an avatar. A result from a subscribed shared formula is "personal"
/// attributed to the formula's owner (the sharer), so its row already shows the sharer's avatar - no separate
/// "shared" kind is needed.</summary>
public record FormulaResultOriginDto(string Kind, string? PersonName, string? PersonPictureUrl);

/// <summary>List/summary shape for a folder's (section's) formula results - the section-scoped twin of
/// <see cref="FormulaResultDto"/>. The generated Markdown <c>Text</c> is fetched separately (a dedicated text
/// endpoint) so listing stays cheap. <c>Status</c> is a <c>FormulaRunStatus</c> name (Generating/Ready/Failed);
/// <c>Error</c> carries the failure reason.</summary>
public record SectionFormulaResultDto(
    Guid Id, Guid SectionId, string Name, Guid? CreatedByUserId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Status, string? Error,
    FormulaResultOriginDto Origin);

/// <summary>A formula result's generated Markdown body, fetched separately from the list/summary DTO.</summary>
public record FormulaResultTextDto(string Text);

/// <summary>Edits a formula result's Markdown body.</summary>
public record UpdateFormulaResultRequest(string Text);

// ---- Webhooks (outbound "Automations") ----

/// <summary>A webhook subscription as listed/returned after creation - never carries the secret (see
/// <see cref="WebhookCreatedDto"/> for the one-time reveal). <c>Scope</c> is a <c>WebhookScope</c> name
/// ("Personal"/"Platform"); <c>SignalFilter</c> is the Workflow Signal keys the subscription routes on
/// (empty for a Personal subscription that hasn't narrowed to any).</summary>
public record WebhookSubscriptionDto(
    Guid Id, string Name, string Url, string[] EventTypes, bool IsActive, int ConsecutiveFailures,
    string? DisabledReason, DateTimeOffset? LastDeliveryAt, string? LastStatus, DateTimeOffset CreatedAt,
    string Scope, string[] SignalFilter, bool IncludeAttendeeContacts, bool IncludeFeedbackText);

/// <summary>Returned only from <c>Create</c> - the plaintext signing <see cref="Secret"/> is shown once and
/// never persisted or returned again.</summary>
public record WebhookCreatedDto(Guid Id, string Name, string Url, string[] EventTypes, string Secret);

public record CreateWebhookRequest(
    string? Name, string Url, string[] EventTypes, bool? IncludeAttendeeContacts = null);

public record UpdateWebhookRequest(
    string? Name, string Url, string[] EventTypes, bool IsActive, bool? IncludeAttendeeContacts = null);

/// <summary>Creates an admin-owned, signal-routed Platform webhook subscription (Phase 3). Requires a
/// non-empty <see cref="SignalFilter"/> whenever any chosen event type is signal-routed - such a
/// subscription with no signal fires on nothing. Event types exempted from that gate
/// (<c>WebhookEventTypes.IsSignalRouted</c> - currently <c>feedback.submitted</c>) fire whatever the filter
/// says, so a subscription made only of those may leave it empty.</summary>
public record CreatePlatformWebhookRequest(
    string? Name, string Url, string[] EventTypes, string[] SignalFilter, bool? IncludeFeedbackText = null);

public record UpdatePlatformWebhookRequest(
    string? Name, string Url, string[] EventTypes, string[] SignalFilter, bool IsActive,
    bool? IncludeFeedbackText = null);

/// <summary>One delivery attempt (or pending/queued item) for a subscription, for the deliveries log.
/// <c>Status</c> is a <c>WebhookDeliveryStatus</c> name (Pending/Succeeded/Failed/...).</summary>
public record WebhookDeliveryDto(
    Guid Id, string EventType, string Status, int AttemptCount, int? ResponseStatus, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt);

// ---- Workflow Signals (admin-defined vocabulary for the formula author's picker) ----

public record WorkflowSignalDto(Guid Id, string Key, string Label, string? Description, bool IsActive);

public record CreateWorkflowSignalRequest(string Key, string Label, string? Description);

public record UpdateWorkflowSignalRequest(string Label, string? Description, bool IsActive);

// ---- Feedback (a user's "something looks/behaves wrong" report) ----

public record CreateFeedbackRequest(string Description, string Route, string Release, string TrailJson);

public record FeedbackDto(Guid Id, Guid UserId, string? UserEmail, DateTimeOffset CreatedAt,
    string Description, string Route, string Release, string TrailJson);

// ---- Platform LLM models (the Models admin page) ----

/// <summary>One configured model. <paramref name="Parameters"/> maps an <c>LlmCallGroup</c> NAME to that
/// group's parameter-layer JSON, with <c>ModelBase</c> holding the model's own defaults - a group absent
/// from the map has no override and inherits.
///
/// The key itself is never returned, only <paramref name="HasApiKey"/>: same write-only contract the
/// per-user key had, so a stored secret cannot leak back out through the admin UI.</summary>
public record LlmModelDto(Guid Id, string Name, string ApiBase, bool HasApiKey, int ContextLength,
    Dictionary<string, string> Parameters, string? DisplayName = null, bool ChatEnabled = false,
    string? Description = null);

/// <summary>Create or replace a model. A null <c>ApiKey</c> on update means "keep the stored key" - the UI
/// was never given it, so it cannot send it back.
///
/// <c>ChatEnabled</c> is deliberately ABSENT. The editor drawer does not show that control, so were it a
/// field here every save from the drawer would post whatever stale value the client held and silently
/// un-offer the model. It has its own route instead.
///
/// <c>Description</c> IS a field here, unlike <c>ChatEnabled</c>: the editor drawer shows the control, so a
/// save posts a value the administrator can actually see on screen.</summary>
public record LlmModelUpsert(string Name, string ApiBase, string? ApiKey, int ContextLength,
    Dictionary<string, string> Parameters, string? DisplayName = null, string? Description = null);

/// <summary>Whether a model appears in the chat model picker.</summary>
public record SetChatEnabledRequest(bool Enabled);

/// <summary>Ask an OpenAI-compatible server what models it has.
///
/// This is the only request in the platform that carries an endpoint the caller chose. See
/// <c>LlmModelDiscoveryClient</c> for why that was accepted here and what bounds it.</summary>
public record DiscoverModelsRequest(string ApiBase, string? ApiKey);

/// <summary>One model an endpoint reported, as offered for import.
///
/// <paramref name="ContextLength"/> is always a usable number, but <paramref name="ContextLengthReported"/>
/// says whether the endpoint actually supplied it or it was defaulted. The distinction matters: the value
/// sizes both the chat dial and the real context budget, so an administrator who cannot tell a measurement
/// from a guess has no reason to correct the guess.</summary>
public record DiscoveredModelDto(string Id, int ContextLength, bool ContextLengthReported, bool AlreadyExists);

/// <summary>What a server reported, and the endpoint the models will be created against.
///
/// <paramref name="ApiBase"/> is the <b>resolved</b> endpoint, which may differ from the one submitted: a
/// server address typed without its <c>/v1</c> is corrected to the path that actually serves
/// <c>/chat/completions</c>. It is returned so the correction is visible in the dialog rather than silent -
/// a model row pointing at the wrong path looks perfectly healthy and simply never answers.</summary>
public record DiscoverModelsResultDto(string ApiBase, IReadOnlyList<DiscoveredModelDto> Models);

/// <summary>Create a model row for each named model on this endpoint. Names are re-checked against the
/// endpoint's own listing, so this cannot be used to create a row for a model it never reported.</summary>
public record ImportModelsRequest(string ApiBase, string? ApiKey, IReadOnlyList<string> Names);

/// <summary>What an import did. <paramref name="NeedContextLength"/> names the models whose context window
/// was defaulted rather than reported, so the administrator knows which to check.</summary>
public record ImportModelsResultDto(int Added, int Skipped, IReadOnlyList<string> NeedContextLength);

/// <summary>Run one sample call against a model, with the parameters an administrator is currently
/// editing rather than the saved ones - so a change can be tried before it is committed.
///
/// Carries NO endpoint, key or model name: those come from the stored row alone. Accepting them here would
/// turn an administrator's session into a way of reaching arbitrary hosts without leaving a model row
/// behind, and the row is the audit trail.
///
/// <c>Parameters</c> is the same group -> layer-JSON map as <see cref="LlmModelUpsert"/>, so the layer walk
/// is identical to the one the saved model would get.</summary>
/// <summary><c>RecordingId</c> is required for Tags, Actions and Summaries, whose test runs the real prompt
/// against a real transcript, and ignored for the four groups that use the built-in sample.</summary>
public record LlmModelTestRequest(string Group, Dictionary<string, string> Parameters, Guid? RecordingId = null);

/// <summary>The recording an administrator has chosen to test models against. <c>Title</c> is the display
/// name (<c>Name ?? Title</c>) so the picker can label the choice without a second fetch; both fields are
/// null when nothing is chosen or the recording has since been deleted.</summary>
public record LlmTestRecordingDto(Guid? RecordingId, string? Title);

/// <summary>Null clears the choice.</summary>
public record SetLlmTestRecordingRequest(Guid? RecordingId);

/// <summary>Which model serves which call group, plus the fallback for groups with no entry. Group names
/// are <c>LlmCallGroup</c> members; <c>ModelBase</c> is rejected - it is a parameter scope, not a call
/// type.</summary>
public record LlmAssignmentsDto(Guid? DefaultModelId, Dictionary<string, Guid> Assignments);
