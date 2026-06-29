using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>Applies automatic speaker identification to a set of recording speakers: each speaker that
/// has a stored embedding and isn't manually named is matched against the owner's voiceprints and
/// labelled (or reverted to anonymous if a previous auto-match no longer holds). Shared by the worker
/// callback and the on-demand "Re-identify" action. Mutates the speakers in place.</summary>
public static class SpeakerLabeling
{
    public static async Task ApplyAsync(
        IEnumerable<Speaker> speakers, Guid userId, ISpeakerIdentifier identifier, CancellationToken ct = default)
    {
        foreach (var sp in speakers)
        {
            if (sp.Embedding is null) continue;
            // "Multiple Speakers" is overlapping audio — never match it against a single-person voiceprint.
            if (sp.IsMultiSpeaker) continue;
            // Only (re)label anonymous or previously-auto speakers — never override a manual name.
            if (!(sp.IdentifiedAuto || sp.DisplayName == sp.Label)) continue;

            var match = await identifier.IdentifyAsync(userId, sp.Embedding, ct);
            if (match is not null)
            {
                sp.ProfileId = match.ProfileId;
                sp.DisplayName = match.Name;
                sp.IdentifiedAuto = true;
            }
            else if (sp.IdentifiedAuto)
            {
                // Previously auto-identified but no longer matches → revert to the anonymous label.
                sp.ProfileId = null;
                sp.DisplayName = sp.Label;
                sp.IdentifiedAuto = false;
            }
        }
    }
}
