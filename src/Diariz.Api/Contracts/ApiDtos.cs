using Diariz.Domain.Entities;

namespace Diariz.Api.Contracts;

// ---- Auth ----
public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt);

// ---- Recordings ----
public record RecordingSummaryDto(
    Guid Id,
    string Title,
    string? Name,
    RecordingSource Source,
    long DurationMs,
    RecordingStatus Status,
    DateTimeOffset CreatedAt);

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
    TranscriptionDto? Current,
    SummaryDto? Summary);

public record RenameSpeakerRequest(string Label, string DisplayName);
public record RenameRecordingRequest(string? Name);
public record RetranscribeRequest(string? Model);
public record UpdateSegmentRequest(string Text);

// ---- User settings (per-user summarisation config) ----
/// <summary>Settings returned to the client. The API key is never exposed — only whether one is set.
/// The Default* fields are the server-wide values, shown as placeholders so the user can see what
/// applies when they leave a field blank (without those defaults being persisted as their own).</summary>
public record UserSettingsDto(
    string? ApiBase, string? Model, bool HasApiKey,
    string? DefaultApiBase, string? DefaultModel, bool ServerHasApiKey);

/// <summary>Update request. ApiKey is tri-state: null = leave unchanged, "" = clear, value = set.</summary>
public record UpdateUserSettingsRequest(string? ApiBase, string? Model, string? ApiKey);
