using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Runs automatic identification over a set of speakers, resolving the operating point from
/// <see cref="PlatformSettings"/> as it goes.
///
/// <para>Exists so callers do not each have to know that identification has thresholds, or where they are
/// stored. The two that run it - the worker callback and the on-demand re-identify - previously injected
/// <c>ISpeakerIdentifier</c> and passed it straight through; they now inject this instead, so nothing had to
/// grow a second dependency to reach the settings.</para></summary>
public interface ISpeakerIdentification
{
    /// <param name="speechByLabel">Speech per diarization label. Supplied by the caller rather than queried
    /// here because the worker callback holds segments that are not saved yet, and a database read would
    /// measure the previous transcription instead of the one being written.</param>
    Task ApplyAsync(
        IEnumerable<Speaker> speakers,
        IReadOnlyDictionary<string, long> speechByLabel,
        CancellationToken ct = default);
}

public class SpeakerIdentification(
    DiarizDbContext db, ISpeakerIdentifier identifier, IPlatformSettingsService settings)
    : ISpeakerIdentification
{
    public async Task ApplyAsync(
        IEnumerable<Speaker> speakers,
        IReadOnlyDictionary<string, long> speechByLabel,
        CancellationToken ct = default)
    {
        var list = speakers as IReadOnlyCollection<Speaker> ?? speakers.ToList();
        var thresholds = IdentificationThresholds.From(await settings.GetAsync(ct));
        await SpeakerLabeling.ApplyAsync(
            list, identifier, thresholds, speechByLabel, await RejectedPairsAsync(list, ct), ct);
    }

    /// <summary>The (speaker, person) pairs someone has already declined, so a re-scan does not hand the same
    /// wrong guess back every time. Scoped to the speakers in hand rather than reading the whole log.</summary>
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
