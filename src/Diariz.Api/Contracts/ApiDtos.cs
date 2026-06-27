using Diariz.Domain.Entities;

namespace Diariz.Api.Contracts;

// ---- Auth ----
public record LoginRequest(string Email, string Password);
public record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt);

// ---- Access requests / account setup ----
public record RequestAccessRequest(string Email);
/// <summary>Whether a setup link is valid (and, if so, the email it's for) — neutral when invalid.</summary>
public record SetupValidateResponse(bool Valid, string? Email);
public record SetupRequest(string Email, string Token, string FullName, string Password);

// ---- Admin user management ----
public record AdminUserDto(
    Guid Id, string Email, string? FullName, string AccountType, UserStatus Status, bool IsEnabled);
public record AddUserRequest(string Email);
public record SetRoleRequest(string Role);
public record SetEnabledRequest(bool IsEnabled);
/// <summary>Result of granting access: whether the link was emailed, and (on the no-SMTP fallback)
/// the setup URL for the admin to share manually.</summary>
public record GrantResultDto(bool Emailed, string? SetupUrl);

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
    string? SectionName);

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
    RecordingStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> SpeakerNames,
    IReadOnlyList<SpeakerInfoDto> Speakers,
    TranscriptionDto? Current,
    SummaryDto? Summary);

public record RenameSpeakerRequest(string Label, string DisplayName);

// ---- Speaker identification (voiceprints) ----
public record SpeakerProfileDto(Guid Id, string Name, int SampleCount);
public record CreateSpeakerProfileRequest(string Name, Guid RecordingId, string Label);
public record AssignSpeakerRequest(Guid? ProfileId);
/// <summary>Per-recording speaker: its label, shown name, the matched voiceprint (if any), and whether
/// the name was set automatically.</summary>
public record SpeakerInfoDto(string Label, string DisplayName, Guid? ProfileId, bool IdentifiedAuto);
public record RenameRecordingRequest(string? Name);
public record RetranscribeRequest(string? Model);
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
