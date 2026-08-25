using Diariz.Domain.Entities;

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

public class SpeakerIdentification(ISpeakerIdentifier identifier, IPlatformSettingsService settings)
    : ISpeakerIdentification
{
    public async Task ApplyAsync(
        IEnumerable<Speaker> speakers,
        IReadOnlyDictionary<string, long> speechByLabel,
        CancellationToken ct = default)
    {
        var thresholds = IdentificationThresholds.From(await settings.GetAsync(ct));
        await SpeakerLabeling.ApplyAsync(speakers, identifier, thresholds, speechByLabel, ct);
    }
}
