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

    /// <summary>The person this speaker is identified as, or null when anonymous. Maps to the
    /// <c>"ProfileId"</c> column - see the table-naming note on <see cref="Entities.Person"/>.</summary>
    public Guid? PersonId { get; set; }
    public Person? Person { get; set; }

    /// <summary>True when <see cref="DisplayName"/>/<see cref="ProfileId"/> were set by automatic
    /// identification (vs. a manual rename/assignment). Lets re-identification and GDPR erasure revert
    /// auto labels without touching names the user set by hand.</summary>
    public bool IdentifiedAuto { get; set; }

    /// <summary>The user has marked this diarization slot as overlapping/simultaneous speech ("Multiple
    /// Speakers"). Such a speaker is never auto-identified nor enrolled into a voiceprint, since its audio
    /// is a mix of people. Setting any explicit name/profile (or unassigning) clears the flag.</summary>
    public bool IsMultiSpeaker { get; set; }

    /// <summary>The audio behind <see cref="Embedding"/> no longer describes this speaker, because a segment
    /// was reassigned into or out of them. Set by per-segment reassignment, surfaced in the People screen's
    /// Voiceprint tab, and cleared when a re-embed job reports back.
    ///
    /// <para>A <em>split</em> sets nothing: the same audio is still attributed to the same speaker, only
    /// divided. Only moving a segment between speakers changes what each one's audio is.</para>
    ///
    /// <para>Nothing recomputes silently - that needs the worker and the original audio - so this is a flag,
    /// not a trigger.</para></summary>
    public bool EmbeddingStale { get; set; }

    /// <summary>A person this speaker may be, offered for confirmation rather than applied.
    ///
    /// <para>Set when the nearest voiceprint lands between the acceptance threshold and the confirmation
    /// band: close enough to be worth asking about, not close enough to name unasked. The speaker stays
    /// <b>anonymous</b> while a suggestion is pending - <see cref="PersonId"/> is untouched - so nothing
    /// downstream treats a maybe as a fact.</para>
    ///
    /// <para>The three suggestion columns are always null together or set together. Cleared by accepting or
    /// rejecting, and a rejected pair is never suggested again (see
    /// <see cref="SpeakerIdentityDecision"/>).</para>
    ///
    /// <para>Maps to the <c>"SuggestedProfileId"</c> column, following the same naming exception as
    /// <see cref="PersonId"/>.</para></summary>
    public Guid? SuggestedPersonId { get; set; }

    /// <summary>Cosine distance to <see cref="SuggestedPersonId"/> when the suggestion was made. Shown to
    /// whoever judges it, and copied onto the decision log so the sweep has the number that was actually on
    /// offer rather than one recomputed against a gallery that has since moved.</summary>
    public double? SuggestedDistance { get; set; }

    public DateTimeOffset? SuggestedAt { get; set; }

    /// <summary>The display name applied when a speaker is flagged <see cref="IsMultiSpeaker"/>. Stored
    /// verbatim so server-side transcript surfaces (exports, email, chat) read it like any other name;
    /// the web localises the in-app affordance.</summary>
    public const string MultiSpeakerName = "Multiple Speakers";
}
