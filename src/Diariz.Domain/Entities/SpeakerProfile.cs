using Pgvector;

namespace Diariz.Domain.Entities;

/// <summary>An enrolled person's voiceprint, scoped to a user. The <see cref="Embedding"/> centroid is
/// the L2-normalised mean of the contributing speakers' embeddings and is matched against new recordings'
/// speakers by cosine distance for automatic identification. Biometric data — erasable for GDPR.</summary>
public class SpeakerProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The room this voiceprint belongs to (its owner's personal room, for now). Plain column - the FK
    /// and the UserId retirement land in Phase 4. Members share a room's voiceprints once shared rooms exist.</summary>
    public Guid RoomId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Centroid voiceprint (ECAPA, 192-d).</summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>Number of contributing speakers averaged into the centroid.</summary>
    public int SampleCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ProfileContribution> Contributions { get; set; } = new List<ProfileContribution>();
}
