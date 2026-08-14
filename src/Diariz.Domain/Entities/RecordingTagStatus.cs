namespace Diariz.Domain.Entities;

/// <summary>Where a <see cref="RecordingTag"/> stands with the user. Tags are extracted automatically but
/// only ever <em>offered</em>: the tag cloud, drill-down and search cover <see cref="Adopted"/> tags only.
/// Stored as ints in Postgres - APPEND ONLY, never renumber.</summary>
public enum RecordingTagStatus
{
    /// <summary>The LLM proposed it; nobody has acted on it. Replaced wholesale by the next extraction and
    /// invisible to every aggregate.</summary>
    Suggested = 0,
    /// <summary>The user picked it (typed it, or promoted a suggestion). Survives a re-transcription and is
    /// the only status the tag cloud counts.</summary>
    Adopted = 1,
    /// <summary>The user rejected it for this recording. Kept as a tombstone so a re-extraction cannot
    /// suggest it again here.</summary>
    Dismissed = 2,
}
