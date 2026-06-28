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

// ---- Admin user management ----
public record AdminUserDto(
    Guid Id, string Email, string? FullName, string AccountType, UserStatus Status, bool IsEnabled,
    long QuotaBytes, long UsedBytes);
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
/// <summary>The signed-in user's storage usage vs their quota (bytes).</summary>
public record StorageUsageDto(long UsedBytes, long QuotaBytes);

// ---- Sections ----
public record SectionDto(Guid Id, string Name);
public record CreateSectionRequest(string Name);
public record RenameSectionRequest(string Name);
public record MoveRecordingRequest(Guid? SectionId);
/// <summary>Reorder/move: set each listed recording's section and position (0..n-1) in one call.</summary>
public record ReorderRecordingsRequest(Guid? SectionId, IReadOnlyList<Guid> OrderedIds);

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
    bool HasActions);

public record SegmentDto(Guid Id, string Speaker, string SpeakerDisplay, long StartMs, long EndMs, string Text);

public record SummaryDto(string Model, string Text, DateTimeOffset CreatedAt);

public record TranscriptionDto(
    Guid Id,
    string Model,
    int Version,
    string? Language,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SegmentDto> Segments);

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
    IReadOnlyList<RecordingActionDto> Actions,
    bool ActionsExtracted);

public record RenameSpeakerRequest(string Label, string DisplayName);

// ---- Action items (extracted from a transcript; user-editable) ----
public record RecordingActionDto(Guid Id, string Text, string Actor, string Deadline, int Ordinal);
public record CreateRecordingActionRequest(string? Text, string? Actor, string? Deadline);
public record UpdateRecordingActionRequest(string? Text, string? Actor, string? Deadline);

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
/// <summary>Per-recording speaker: its label, shown name, the matched voiceprint (if any), and whether
/// the name was set automatically.</summary>
public record SpeakerInfoDto(string Label, string DisplayName, Guid? ProfileId, bool IdentifiedAuto);
public record RenameRecordingRequest(string? Name);
/// <summary>Diarization speaker-count hints. Either bound may be null (= no bound / auto).</summary>
public record SpeakerHints(int? Min, int? Max);
/// <summary>Re-transcribe options. <see cref="Speakers"/> is tri-state: omit/null = keep the recording's
/// existing hints; present = set them (an object with null Min/Max means "back to automatic").</summary>
public record RetranscribeRequest(string? Model, SpeakerHints? Speakers = null);
public record UpdateSegmentRequest(string Text);

// ---- User settings (per-user summarisation config) ----
/// <summary>Settings returned to the client. The API key is never exposed — only whether one is set.
/// The Default* fields are the server-wide values, shown as placeholders so the user can see what
/// applies when they leave a field blank (without those defaults being persisted as their own).</summary>
public record UserSettingsDto(
    string? ApiBase, string? Model, bool HasApiKey,
    string? DefaultApiBase, string? DefaultModel, bool ServerHasApiKey,
    int? ContextWindow, int DefaultContextWindow);

/// <summary>Update request. ApiKey is tri-state: null = leave unchanged, "" = clear, value = set.
/// ContextWindow: null/&lt;=0 clears the per-user override (falls back to the server default).</summary>
public record UpdateUserSettingsRequest(string? ApiBase, string? Model, string? ApiKey, int? ContextWindow = null);

// ---- Chat ----
public record ChatTurnDto(string Role, string Content);

/// <summary>The context a chat turn (or a saved conversation) runs against.</summary>
public record SavedChatContextDto(
    IReadOnlyList<Guid> RecordingIds, string? AttachmentName, string? AttachmentText);

/// <summary>A streaming chat request: the selected context + the full conversation so far.</summary>
public record ChatStreamRequest(
    IReadOnlyList<Guid> RecordingIds,
    string? AttachmentName,
    string? AttachmentText,
    IReadOnlyList<ChatTurnDto> Messages);

/// <summary>Extracted attachment text returned to the client (held and resent with each turn).</summary>
public record ChatAttachmentDto(string Name, int Chars, string Text);

public record ChatConversationSummaryDto(Guid Id, string Title, DateTimeOffset UpdatedAt);

public record ChatConversationDto(
    Guid Id, string Title, IReadOnlyList<ChatTurnDto> Messages, SavedChatContextDto Context,
    DateTimeOffset UpdatedAt);

public record SaveChatConversationRequest(
    IReadOnlyList<ChatTurnDto> Messages, SavedChatContextDto Context);

public record SaveChatConversationResult(Guid Id, string Title);
