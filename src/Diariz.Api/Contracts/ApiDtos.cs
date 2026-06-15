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
    long DurationMs,
    RecordingStatus Status,
    DateTimeOffset CreatedAt);

public record SegmentDto(string Speaker, string SpeakerDisplay, long StartMs, long EndMs, string Text);

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
    long DurationMs,
    RecordingStatus Status,
    string? Error,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> SpeakerNames,
    TranscriptionDto? Current);

public record RenameSpeakerRequest(string Label, string DisplayName);
public record RetranscribeRequest(string? Model);
