using Pgvector;

namespace Diariz.Domain.Entities;

/// <summary>A speaker (from one recording) explicitly enrolled into a <see cref="SpeakerProfile"/>. Holds a
/// snapshot of that speaker's embedding so the profile centroid can be recomputed if contributions change,
/// without re-running the worker. Only explicit contributions build a centroid — never auto-matches.</summary>
public class ProfileContribution
{
    public Guid Id { get; set; }

    public Guid ProfileId { get; set; }
    public SpeakerProfile? Profile { get; set; }

    /// <summary>The contributing per-recording speaker.</summary>
    public Guid SpeakerId { get; set; }
    public Speaker? Speaker { get; set; }

    /// <summary>The recording this contribution came from (for display in the People screen).</summary>
    public Guid RecordingId { get; set; }

    /// <summary>Snapshot of the contributing speaker's embedding (ECAPA, 192-d).</summary>
    public Vector Embedding { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
