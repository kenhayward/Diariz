using Pgvector;

namespace Diariz.Domain.Entities;

/// <summary>Someone who appears in meetings: a name plus optional contact details, and an <em>optional</em>
/// voiceprint. The biometric is the removable part - a person may never have been voice-printed, may have
/// opted out, or may have had their voiceprint erased, and in every case the person record survives so the
/// transcripts they appear in keep making sense.
///
/// <para><b>Table naming.</b> This maps to the <c>SpeakerProfiles</c> table, not <c>People</c>. Renaming it
/// would be a destructive rename, which forces a <c>MaintenanceController.CurrentFormat</c> bump and
/// hard-rejects every backup archive taken before that point, with no conversion path. The mapping is pinned
/// in <c>DiarizDbContext.OnModelCreating</c>; see <c>docs/Data_Schema.md</c>.</para>
///
/// <para><b>Two columns mean different things and are easy to confuse in raw SQL:</b>
/// <see cref="CreatedByUserId"/> is column <c>"UserId"</c> and records <em>who enrolled this person</em>;
/// <see cref="LinkedUserId"/> records <em>which account this person is</em>. They are rarely the same
/// value.</para></summary>
public class Person
{
    public Guid Id { get; set; }

    /// <summary>Who enrolled this person. Column <c>"UserId"</c>. <b>Provenance only</b> - never filter on
    /// it. Nullable so the backfill can provision a person nobody enrolled, but deleting the account still
    /// cascades the person away, as it always has.</summary>
    public Guid? CreatedByUserId { get; set; }
    public ApplicationUser? CreatedBy { get; set; }

    /// <summary>The room this person was first enrolled from. <b>Provenance only</b>, like
    /// <see cref="CreatedByUserId"/> - retained because dropping the column would break the backup fence,
    /// not because anything reads it.</summary>
    public Guid? RoomId { get; set; }

    /// <summary>The account this person <em>is</em>, or null for someone with no login (a client, a guest).
    /// Unique among non-null values: one account is one person. Provisioned and kept in sync by
    /// <c>IPeopleDirectory</c>, which is why <see cref="Name"/> and <see cref="Email"/> are read-only in the
    /// UI for a linked person - they follow the account.</summary>
    public Guid? LinkedUserId { get; set; }
    public ApplicationUser? LinkedUser { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Title { get; set; }
    public string? CompanyName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>True for a colleague, false for an external party. Drives routing decisions downstream (who
    /// a workflow may mail), so it is carried on the webhook attendee payload.</summary>
    public bool IsInternal { get; set; }

    /// <summary>The person has asked not to be voice-printed. Setting it erases any existing voiceprint and
    /// its samples, and excludes them from automatic identification from then on. Clearing it does not bring
    /// anything back - the biometric is gone, and they would have to be enrolled again.</summary>
    public bool VoiceprintOptOut { get; set; }

    /// <summary>Centroid voiceprint (ECAPA, 192-d), or null when this person has none. Null is the ordinary
    /// case for someone added to the directory by hand.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>Number of voice samples averaged into the centroid. Kept in step with
    /// <see cref="VoiceSamples"/> by <c>IPeopleDirectory.RecomputeVoiceprintAsync</c>.</summary>
    public int SampleCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<VoiceSample> VoiceSamples { get; set; } = new List<VoiceSample>();
}
