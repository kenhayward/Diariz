using Pgvector;

namespace Diariz.Domain.Entities;

/// <summary>A speaker (from one recording) explicitly enrolled into a <see cref="Person"/>'s voiceprint.
/// Holds a snapshot of that speaker's embedding so the centroid can be recomputed when samples change,
/// without re-running the worker. Only explicit enrolments build a centroid - never auto-matches, or
/// identification would compound its own mistakes.
///
/// <para>Maps to the <c>ProfileContributions</c> table, and <see cref="PersonId"/> to its <c>"ProfileId"</c>
/// column - see the table-naming note on <see cref="Person"/>.</para></summary>
public class VoiceSample
{
    public Guid Id { get; set; }

    public Guid PersonId { get; set; }
    public Person? Person { get; set; }

    /// <summary>The contributing per-recording speaker.</summary>
    public Guid SpeakerId { get; set; }
    public Speaker? Speaker { get; set; }

    /// <summary>The recording this sample came from (for display in the People screen). Deliberately has no
    /// FK: the row is stitched to its recording name in memory, and a deleted recording renders as
    /// "(deleted recording)" rather than taking the sample with it.</summary>
    public Guid RecordingId { get; set; }

    /// <summary>Snapshot of the contributing speaker's embedding (ECAPA, 192-d).</summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>The spans of this recording's audio that train the voiceprint, as JSON. <b>Null means the
    /// whole speaker</b> - the state every sample enrolled before selection existed is in, and the
    /// behaviour that has always applied, so nothing needed backfilling.
    ///
    /// <para>Spans, not segment ids: segment rows belong to a transcription <em>version</em>, and a
    /// re-transcribe replaces every one of them, where wall-clock times survive. Read and written through
    /// <c>VoiceprintSpans</c>.</para></summary>
    public string? SpansJson { get; set; }

    /// <summary>How much audio the last embedding actually consumed, in ms, or null while a recompute is
    /// queued and has not reported back.
    ///
    /// <para>Two jobs, deliberately. It is the honest figure behind "using 1:20 of the 4:12 selected" -
    /// the worker still caps how much it pools, so a selection is not necessarily what was used. And
    /// because the enqueue clears it and the callback sets it, it is also the <b>pending marker</b>, which
    /// means a recompute in flight survives a page reload instead of living only in component
    /// state.</para></summary>
    public int? UsedMs { get; set; }

    /// <summary>When a user dropped this sample from training, or null while it still trains the voiceprint.
    ///
    /// <para>Excluded rather than deleted on purpose. The row records that a human once asserted this
    /// speaker was this person; deleting it loses that assertion, and a later re-scan would then be free to
    /// silently re-add what someone deliberately removed. It also makes re-including a toggle rather than a
    /// fresh enrolment.</para>
    ///
    /// <para><b>Store UTC.</b> Npgsql rejects a non-zero-offset DateTimeOffset for a timestamptz column and
    /// throws at SaveChanges - the in-memory provider will not catch it.</para></summary>
    public DateTimeOffset? ExcludedAt { get; set; }

    /// <summary>When a re-embed was queued, or null when none is in flight.
    ///
    /// <para>Stored rather than inferred. It used to be read off <see cref="SpansJson"/> and
    /// <see cref="UsedMs"/>, neither of which can express it: selecting the <b>whole speaker</b> serialises
    /// to a null <c>SpansJson</c>, so the commonest case evaluated to "not pending" and the UI said nothing
    /// at all when the button was pressed. <see cref="UsedMs"/> cannot stand in either - a sample enrolled
    /// straight from a speaker is never re-embedded, so it is null for almost every row.</para>
    ///
    /// <para><b>Store UTC.</b> Npgsql rejects a non-zero-offset DateTimeOffset for a timestamptz.</para></summary>
    public DateTimeOffset? RecomputeQueuedAt { get; set; }

    /// <summary>When the last re-embed failed, or null if the last one succeeded or none has run.
    ///
    /// <para>A failure leaves the vector alone - a failed recompute must not destroy a working voiceprint -
    /// so without this the row is indistinguishable from one that never ran. The callback previously wrote
    /// <c>UsedMs = 0</c> purely to stop the row reading as pending, which rendered as "trains on 0:00": a
    /// confident figure for audio that was never measured.</para>
    ///
    /// <para><b>Store UTC.</b></para></summary>
    public DateTimeOffset? RecomputeFailedAt { get; set; }

    /// <summary>When a human listened and vouched that this recording really is this person, or null if
    /// nobody has.
    ///
    /// <para><b>A different assertion from <see cref="ExcludedAt"/>.</b> Excluding asks whether the audio is
    /// good enough to learn from; this asks whether it is the right person. A recording can be genuinely
    /// them and still be too noisy to train on, and the two must be settable independently.</para>
    ///
    /// <para>It exists because distance provably cannot separate a second microphone from a second human -
    /// the finding behind the impostor check - so only someone who has listened can settle it. Multi-template
    /// voiceprints will gate template-seeding on this; until then it takes the recording out of the review
    /// queue.</para>
    ///
    /// <para><b>Store UTC.</b> Npgsql rejects a non-zero-offset DateTimeOffset for a timestamptz.</para></summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Who vouched for it. Recorded because the value of the gate is that a <em>named</em> human
    /// listened; an anonymous flag would be an assertion nobody is accountable for. Deliberately no FK - the
    /// record of who asserted it must outlive their account, exactly as the sample outlives its recording.
    /// </summary>
    public Guid? ConfirmedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
