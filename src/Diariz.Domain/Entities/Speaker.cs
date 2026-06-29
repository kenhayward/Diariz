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

    /// <summary>The user has marked this diarization slot as overlapping/simultaneous speech ("Multiple
    /// Speakers"). Such a speaker is never auto-identified nor enrolled into a voiceprint, since its audio
    /// is a mix of people. Setting any explicit name/profile (or unassigning) clears the flag.</summary>
    public bool IsMultiSpeaker { get; set; }

    /// <summary>The display name applied when a speaker is flagged <see cref="IsMultiSpeaker"/>. Stored
    /// verbatim so server-side transcript surfaces (exports, email, chat) read it like any other name;
    /// the web localises the in-app affordance.</summary>
    public const string MultiSpeakerName = "Multiple Speakers";
}
