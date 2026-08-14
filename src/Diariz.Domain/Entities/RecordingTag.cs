namespace Diariz.Domain.Entities;

/// <summary>A topic tag on a recording. The LLM extracts candidates from the transcript, but a tag only
/// counts once the user has adopted it: <see cref="Status"/> separates a machine suggestion from the user's
/// own tag, and only <see cref="RecordingTagStatus.Adopted"/> rows reach the cross-transcript tag cloud.
/// A (re-)transcription replaces the <see cref="RecordingTagStatus.Suggested"/> rows only, so hand-applied
/// tags and dismissals survive it.</summary>
public class RecordingTag
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>The tag text. Every tag written from now on is normalised at write time - internal
    /// whitespace collapsed to hyphens, case preserved as written, never a space - whether it started as a
    /// machine suggestion or was typed by hand (see <c>TagText.Normalize</c>). A row written before this
    /// normalisation existed can still hold un-normalised text with a space in it ("Data Collection"), so
    /// nothing may assume the stored value is already normalised: every comparison re-normalises both sides
    /// before matching, case-insensitively. Unique per recording, case-insensitively.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>For a suggestion, the model's relative salience within this recording (0-1, clamped on
    /// ingest) - it orders the hint list. For an adopted tag, always 1.0, so the cloud's summed weight
    /// equals the number of recordings carrying the tag and sizes words by how often the user used them.</summary>
    public double Weight { get; set; }

    /// <summary>Sort order within the recording (0-based, the LLM's weight-descending order).</summary>
    public int Ordinal { get; set; }

    /// <summary>Whether this is a machine suggestion, the user's own tag, or a dismissal tombstone.</summary>
    public RecordingTagStatus Status { get; set; } = RecordingTagStatus.Suggested;

    /// <summary>When the user adopted it; null for suggestions and dismissals. Orders the chips in the hub's
    /// tag popover - <see cref="CreatedAt"/> cannot, because a promoted suggestion was created whenever the
    /// extraction happened to run.</summary>
    public DateTimeOffset? AdoptedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
