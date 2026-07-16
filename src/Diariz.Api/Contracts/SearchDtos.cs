namespace Diariz.Api.Contracts;

// ---- Search ----

/// <summary>A folder whose *name* matched the query. Tapping one drills the caller to it, so it carries only
/// what a folder row needs.</summary>
/// <param name="Breadcrumb">Ancestor names, root-first, excluding <paramref name="Name"/> itself.</param>
/// <param name="RecordingCount">Everything underneath, including sub-folders' recordings - the same promise the
/// drill-in list's count badge makes.</param>
public record FolderHitDto(
    Guid Id,
    string Name,
    Guid? ParentId,
    Guid RoomId,
    string RoomName,
    IReadOnlyList<string> Breadcrumb,
    int RecordingCount);

/// <summary>A recording whose transcript matched, with the best-matching passage as a snippet.</summary>
/// <param name="Breadcrumb">The folder path the recording lives in, root-first. Empty when the recording sits in
/// no folder, and also when its folder belongs to a room the caller cannot see - a recording shared into my room
/// may still carry a SectionId from someone else's, and its name must not leak.</param>
/// <param name="Snippet">Plain text, never markup: the client highlights the match itself. <c>SnippetStartMs</c>
/// is where it sits in the recording, so the client can deep-link to the moment.</param>
public record RecordingSearchHitDto(
    Guid RecordingId,
    string Name,
    DateTimeOffset CreatedAt,
    long DurationMs,
    Guid? SectionId,
    string? SectionName,
    IReadOnlyList<string> Breadcrumb,
    string? Snippet,
    long SnippetStartMs,
    string? SpeakerName,
    double Score);

/// <summary>A search result set. <paramref name="Scope"/> echoes what was actually searched
/// (<c>folder</c> | <c>room</c> | <c>everywhere</c>) so the UI can show the scope it got rather than the one it
/// asked for.</summary>
public record SearchResponse(
    string Query,
    string Scope,
    IReadOnlyList<FolderHitDto> Folders,
    IReadOnlyList<RecordingSearchHitDto> Recordings);
