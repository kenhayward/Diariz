using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>What a re-scan did, or would do.</summary>
/// <param name="Scanned">Speakers considered - anonymous, unlinked, and carrying an embedding.</param>
/// <param name="Applied">Matches close enough to name automatically.</param>
/// <param name="Suggested">Matches queued for someone to confirm.</param>
public record RescanReport(int Scanned, int Applied, int Suggested)
{
    /// <summary>Considered and left alone: too distant, too ambiguous, too little speech, or already
    /// declined.</summary>
    public int Untouched => Scanned - Applied - Suggested;
}

/// <summary>Re-runs identification across every speaker that could still be identified.
///
/// <para>Identification otherwise happens exactly once, in the transcription callback, so enrolling a person
/// today never revisits yesterday's recordings. On the measured instance that left 38 speakers sitting inside
/// the acceptance threshold, anonymous and unlinked - matches that already qualified and were never
/// applied.</para>
///
/// <para><b>A re-scan adds; it never revokes.</b> That guarantee is structural rather than a flag: the query
/// only selects speakers that are already anonymous and unlinked, so there is no label present for it to take
/// away. Revoking a stale automatic label stays where it belongs, at transcription time - a knob change must
/// not mass-unlabel history.</para>
///
/// <para>Synchronous on purpose. Ranking costs roughly 0.35 ms per 1,000 gallery rows per speaker, so a
/// thousand-speaker platform is well under a second, and a dry run that had to be polled for would be a worse
/// preview than one that simply answers.</para></summary>
public class IdentificationRescan(
    DiarizDbContext db, ISpeakerIdentifier identifier, IPlatformSettingsService settings)
{
    public async Task<RescanReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var thresholds = IdentificationThresholds.From(await settings.GetAsync(ct));

        // Anonymous, unlinked, and never auto-labelled. Anything else either has a name someone chose or a
        // name identification already applied, and re-scanning those could only take something away.
        var speakers = await db.Speakers
            .Where(s => s.Embedding != null
                        && !s.IsMultiSpeaker
                        && s.PersonId == null
                        && !s.IdentifiedAuto
                        && s.DisplayName == s.Label)
            .ToListAsync(ct);

        if (speakers.Count == 0) return new RescanReport(0, 0, 0);

        var rejected = await RejectedPairsAsync(speakers, ct);

        // Speech is per recording, so read it once per recording rather than once per speaker.
        var speech = new Dictionary<Guid, Dictionary<string, long>>();
        foreach (var recordingId in speakers.Select(s => s.RecordingId).Distinct())
            speech[recordingId] = await SpeakerSpeech.ForRecordingAsync(db, recordingId, ct);

        var applied = 0;
        var suggested = 0;

        foreach (var sp in speakers)
        {
            var before = (sp.PersonId, sp.SuggestedPersonId);

            await SpeakerLabeling.ApplyAsync(
                [sp], identifier, thresholds,
                speech.GetValueOrDefault(sp.RecordingId) ?? [], rejected, ct);

            if (sp.PersonId != before.PersonId) applied++;
            else if (sp.SuggestedPersonId != before.SuggestedPersonId && sp.SuggestedPersonId is not null)
                suggested++;
        }

        if (dryRun)
            // Nothing is written, so the counts describe a change that has not happened. Discarding the
            // tracked entities is what keeps a later save on this context from committing it by accident.
            db.ChangeTracker.Clear();
        else
            await db.SaveChangesAsync(ct);

        return new RescanReport(speakers.Count, applied, suggested);
    }

    private async Task<IReadOnlySet<(Guid SpeakerId, Guid PersonId)>> RejectedPairsAsync(
        IReadOnlyCollection<Speaker> speakers, CancellationToken ct)
    {
        var ids = speakers.Select(s => s.Id).ToList();
        var rows = await db.SpeakerIdentityDecisions
            .Where(d => ids.Contains(d.SpeakerId) && d.Decision == IdentityDecisionKind.Rejected)
            .Select(d => new { d.SpeakerId, d.PersonId })
            .ToListAsync(ct);

        return rows.Select(r => (r.SpeakerId, r.PersonId)).ToHashSet();
    }
}
