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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
