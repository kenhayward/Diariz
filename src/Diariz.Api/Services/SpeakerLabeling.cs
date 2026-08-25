using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>Applies automatic speaker identification to a set of recording speakers: each speaker that has a
/// stored embedding and isn't manually named is ranked against the platform's voiceprints, and the verdict
/// from <see cref="IdentificationRules"/> is applied - labelled, left alone, or reverted to anonymous if a
/// previous auto-match no longer holds. Shared by the worker callback and the on-demand "Re-identify" action.
/// Mutates the speakers in place.
///
/// <para>The policy lives in <see cref="IdentificationRules"/> and the ranking in
/// <see cref="ISpeakerIdentifier"/>; this is only the part that knows what a <see cref="Speaker"/> row is.</para></summary>
public static class SpeakerLabeling
{
    public static async Task ApplyAsync(
        IEnumerable<Speaker> speakers,
        ISpeakerIdentifier identifier,
        IdentificationThresholds thresholds,
        IReadOnlyDictionary<string, long> speechByLabel,
        IReadOnlySet<(Guid SpeakerId, Guid PersonId)>? rejected = null,
        CancellationToken ct = default)
    {
        foreach (var sp in speakers)
        {
            if (sp.Embedding is null) continue;
            // "Multiple Speakers" is overlapping audio — never match it against a single-person voiceprint.
            if (sp.IsMultiSpeaker) continue;
            // Only (re)label anonymous or previously-auto speakers — never override a manual name.
            if (!(sp.IdentifiedAuto || sp.DisplayName == sp.Label)) continue;

            // Two candidates: the best, and the runner-up the margin is measured against.
            var ranked = await identifier.RankAsync(sp.Embedding, take: 2, ct);
            var verdict = IdentificationRules.Decide(
                ranked, SpeakerSpeech.MsFor(speechByLabel, sp.Label), thresholds);

            // A suggestion someone has already declined is not offered again - otherwise every re-scan hands
            // back the same wrong guess and the review queue is unclearable by construction. Deliberately
            // only blocks the *suggestion*: if the voiceprint later improves to an outright match, that is
            // new evidence, and one dismissal should not withhold it forever.
            if (verdict.Outcome == IdentificationOutcome.Suggest
                && rejected is not null
                && rejected.Contains((sp.Id, verdict.Match!.PersonId)))
                verdict = new IdentificationVerdict(IdentificationOutcome.Ignore, null);

            if (verdict.Outcome == IdentificationOutcome.Accept)
            {
                sp.PersonId = verdict.Match!.PersonId;
                sp.DisplayName = verdict.Match.Name;
                sp.IdentifiedAuto = true;
                ClearSuggestion(sp); // it was applied; there is nothing left to ask about
            }
            else if (verdict.Outcome == IdentificationOutcome.Suggest)
            {
                // A borderline distance is not enough to keep a name that was applied at full confidence, so
                // an existing auto-label reverts first. The speaker then sits exactly where a fresh borderline
                // match would leave it: anonymous, with a suggestion waiting to be judged.
                RevertAutoLabel(sp);

                sp.SuggestedPersonId = verdict.Match!.PersonId;
                sp.SuggestedDistance = verdict.Match.Distance;
                sp.SuggestedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                // Nothing close enough. Any previous guess no longer holds, so stop asking about it.
                ClearSuggestion(sp);
                RevertAutoLabel(sp);
            }
        }
    }

    /// <summary>Undoes a name automatic identification applied. Never touches a name someone typed - the
    /// eligibility check above has already excluded those.</summary>
    private static void RevertAutoLabel(Speaker sp)
    {
        if (!sp.IdentifiedAuto) return;
        sp.PersonId = null;
        sp.DisplayName = sp.Label;
        sp.IdentifiedAuto = false;
    }

    /// <summary>The three suggestion columns are always null together or set together.</summary>
    private static void ClearSuggestion(Speaker sp)
    {
        sp.SuggestedPersonId = null;
        sp.SuggestedDistance = null;
        sp.SuggestedAt = null;
    }
}
