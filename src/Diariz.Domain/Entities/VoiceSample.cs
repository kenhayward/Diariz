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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
