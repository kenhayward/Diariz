using Pgvector;

namespace Diariz.Domain.Entities;

/// <summary>
/// Maps a diarization label (e.g. "SPEAKER_00") to a user-supplied display name
/// for a given recording. Lets the user rename "Speaker 1" to "Alice".
/// </summary>
public class Speaker
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>The raw diarization label this mapping applies to.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>User-facing display name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>This speaker's voice embedding (ECAPA, 192-d) from the worker. Null until computed.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>The enrolled person this speaker is identified as, or null when anonymous.</summary>
    public Guid? ProfileId { get; set; }
    public SpeakerProfile? Profile { get; set; }

    /// <summary>True when <see cref="DisplayName"/>/<see cref="ProfileId"/> were set by automatic
    /// identification (vs. a manual rename/assignment). Lets re-identification and GDPR erasure revert
    /// auto labels without touching names the user set by hand.</summary>
    public bool IdentifiedAuto { get; set; }
}
