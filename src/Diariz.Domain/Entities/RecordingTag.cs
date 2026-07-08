namespace Diariz.Domain.Entities;

/// <summary>A weighted topic tag the LLM extracted from a recording's transcript, powering the
/// cross-transcript tag cloud. Machine-generated only (never user-edited): a (re-)transcription
/// replaces the recording's whole tag set. <see cref="Weight"/> is the per-recording salience the
/// model assigned (0-1); the cloud aggregates it across recordings.</summary>
public class RecordingTag
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>Canonical tag text (Title Case, 1-2 words per the extraction prompt).</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Relative salience within this recording, 0-1 (clamped on ingest).</summary>
    public double Weight { get; set; }

    /// <summary>Sort order within the recording (0-based, the LLM's weight-descending order).</summary>
    public int Ordinal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
