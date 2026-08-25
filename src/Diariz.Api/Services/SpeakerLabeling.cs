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

            if (verdict.Outcome == IdentificationOutcome.Accept)
            {
                sp.PersonId = verdict.Match!.PersonId;
                sp.DisplayName = verdict.Match.Name;
                sp.IdentifiedAuto = true;
            }
            else if (sp.IdentifiedAuto)
            {
                // Previously auto-identified but no longer accepted → revert to the anonymous label. A
                // *suggestion* is not good enough to keep a name that was applied at full confidence.
                sp.PersonId = null;
                sp.DisplayName = sp.Label;
                sp.IdentifiedAuto = false;
            }
        }
    }
}
