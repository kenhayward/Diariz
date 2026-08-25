namespace Diariz.Domain.Entities;

/// <summary>What a person decided about a suggested identity. <b>Append only, never renumber</b> - the stored
/// ints are read back by the calibration sweep.</summary>
public enum IdentityDecisionKind
{
    /// <summary>"That is not them." A <b>labelled hard negative</b>, and the only source of them the platform
    /// has - every manually-linked speaker is a positive.</summary>
    Rejected = 0,

    /// <summary>"Yes, that is them."</summary>
    Accepted = 1,
}

/// <summary>One human decision about whether a diarized speaker is a given person.
///
/// <para>This is what makes the confirmation band worth having. Auto-identification's own output cannot grade
/// it - that is the system marking its own homework - so a calibrated threshold needs decisions a person
/// actually made, each with the distance that was on offer when they made it.</para>
///
/// <para><see cref="Distance"/> is recorded at the moment of the decision and <b>never recomputed</b>. The
/// gallery moves as people are enrolled and voiceprints are retrained, so a number computed later would
/// describe a different question than the one that was answered.</para>
///
/// <para>Also load-bearing at runtime: a rejected <c>(speaker, person)</c> pair is never suggested again, so
/// declining a suggestion makes it stay declined.</para></summary>
public class SpeakerIdentityDecision
{
    public Guid Id { get; set; }

    public Guid SpeakerId { get; set; }
    public Speaker? Speaker { get; set; }

    /// <summary>The person the suggestion named. Maps to the <c>"ProfileId"</c> column - see the table-naming
    /// note on <see cref="Person"/>.</summary>
    public Guid PersonId { get; set; }
    public Person? Person { get; set; }

    public IdentityDecisionKind Decision { get; set; }

    /// <summary>Cosine distance between the speaker and the person at the moment of the decision.</summary>
    public double Distance { get; set; }

    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Who decided, or null once that account is gone. Provenance rather than the point: the
    /// labelled pair stays valid evidence whether or not the person who produced it still has an
    /// account.</summary>
    public Guid? DecidedByUserId { get; set; }
}
