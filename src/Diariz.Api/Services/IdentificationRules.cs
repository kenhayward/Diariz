using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>What to do about a voice match.</summary>
public enum IdentificationOutcome
{
    /// <summary>Not close enough, too ambiguous, or too little speech to judge.</summary>
    Ignore,

    /// <summary>Close enough to be worth asking about, not close enough to apply unasked.</summary>
    Suggest,

    /// <summary>Close enough to apply automatically.</summary>
    Accept,
}

/// <summary>One <b>person's</b> best distance to the probe. A list of these is ranked ascending with exactly
/// one entry per person - collapsing a person's several voiceprints to their nearest is the caller's job, and
/// <see cref="IdentificationRules.Decide"/> relies on it for the margin.</summary>
public record RankedCandidate(Guid PersonId, string Name, double Distance);

/// <summary>The operating point, read from <see cref="PlatformSettings"/> so it can be calibrated without a
/// redeploy and there is exactly one copy of each number.</summary>
public record IdentificationThresholds(double Accept, double Suggest, double Margin, int MinSpeechMs)
{
    public static IdentificationThresholds From(PlatformSettings s) =>
        new(s.IdentificationThreshold, s.IdentificationConfirmBand, s.IdentificationMargin,
            s.IdentificationMinSpeechMs);
}

public record IdentificationVerdict(IdentificationOutcome Outcome, RankedCandidate? Match);

/// <summary>Whether a voice match is applied, offered for confirmation, or dropped.
///
/// <para>Pure on purpose. Identification's precision was never the problem - when it fired it was almost
/// always right - so the policy that decides <em>when</em> it fires is the part worth being able to reason
/// about, test exhaustively, and later sweep across recorded decisions.</para></summary>
public static class IdentificationRules
{
    private static readonly IdentificationVerdict Nothing = new(IdentificationOutcome.Ignore, null);

    /// <param name="ranked">Candidates ascending by distance, <b>one per person</b>.</param>
    /// <param name="speechMs">Total speech attributed to the probe speaker.</param>
    public static IdentificationVerdict Decide(
        IReadOnlyList<RankedCandidate> ranked, long speechMs, IdentificationThresholds t)
    {
        if (ranked.Count == 0) return Nothing;

        // Too short to be worth scoring at all. Accuracy climbs steeply up to 10-20 seconds of speech, so a
        // confident number derived from a second and a half is confidence in noise - and if it were accepted,
        // that noise would go on to train the voiceprint it matched.
        if (speechMs < t.MinSpeechMs) return Nothing;

        var best = ranked[0];
        if (best.Distance > t.Suggest) return Nothing;

        // The runner-up is the next PERSON, never another voiceprint of the same one. Callers collapse a
        // person's templates to their best before ranking, so two recordings of one human cannot look like a
        // confusable pair and veto themselves.
        //
        // Applied to suggestions as well as accepts: a borderline match that is also ambiguous would put a
        // coin-flip in front of someone as though it were a considered guess, and their answer to it becomes
        // labelled training data.
        if (ranked.Count > 1 && ranked[1].Distance - best.Distance < t.Margin) return Nothing;

        return new IdentificationVerdict(
            best.Distance <= t.Accept ? IdentificationOutcome.Accept : IdentificationOutcome.Suggest,
            best);
    }
}
