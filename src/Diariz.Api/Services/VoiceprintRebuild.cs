using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Reconciles any voiceprint whose stored centroid was built from a sample that no longer counts as
/// training data.
///
/// <para><see cref="VoiceprintTraining"/> fixes what is computed from now on. It does not touch the centroids
/// already stored, which on the instance this was written for were built from six samples whose speakers have
/// since been unassigned or reassigned - and nothing would have recomputed them until the next time somebody
/// happened to edit that person.</para>
///
/// <para><b>Idempotent in effect, not in selection.</b> The rejected sample row is deliberately kept (it is
/// the record that someone once asserted this speaker was this person, and the Voiceprint tab lists it), so
/// the same people are found on every boot. Recomputing an already-converged centroid through the one
/// derivation that produced it is a no-op, which is what makes running this unconditionally safe - and it
/// needs no run-once marker, unlike a <c>Seeder</c>-style backfill that would re-apply itself.</para>
///
/// <para>It deliberately does not compute a centroid itself. There is exactly one derivation, in
/// <see cref="IPeopleDirectory.RecomputeVoiceprintAsync"/>; a second one written in SQL would agree with it
/// by luck.</para></summary>
public static class VoiceprintRebuild
{
    /// <summary>Recomputes every person holding a sample the training rule rejects. Returns how many.</summary>
    public static async Task<int> RunAsync(
        DiarizDbContext db, IPeopleDirectory people, ILogger logger, CancellationToken ct = default)
    {
        // Only people with something wrong - this must not become "recompute the whole directory on every
        // boot", which would be a slow start-up that also hides the problem it is here to correct.
        var stale = (await db.VoiceSamples
                .Where(v => v.ExcludedAt == null)
                .Join(db.Speakers, v => v.SpeakerId, s => s.Id, (v, s) => new { Sample = v, Speaker = s })
                .ToListAsync(ct))
            .Where(x => !VoiceprintTraining.Trains(x.Sample, x.Speaker))
            .Select(x => x.Sample.PersonId)
            .Distinct()
            .ToList();

        foreach (var personId in stale)
            await people.RecomputeVoiceprintAsync(personId, ct);

        if (stale.Count > 0)
            logger.LogInformation(
                "Reconciled {Count} voiceprint(s) holding a sample whose speaker no longer names them.",
                stale.Count);

        return stale.Count;
    }
}
