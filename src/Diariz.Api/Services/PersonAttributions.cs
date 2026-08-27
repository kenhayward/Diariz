using Diariz.Api.Contracts;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>One attributed speaker, as the controller has already read it from the database.</summary>
/// <param name="StillLinked">Whether the speaker still names this person - see
/// <see cref="VoiceprintTraining.StillLinked"/>. False for one that has been unassigned, reassigned, or
/// marked as overlapping speech.</param>
public record AttributionInput(
    Guid SpeakerId, Guid RecordingId, string Label, bool IdentifiedAuto, bool IsMultiSpeaker, long SpeechMs,
    bool StillLinked);

/// <summary>What a person's Voiceprint tab lists: every speaker attributed to them, with whether it
/// currently trains the voiceprint.
///
/// <para>Pure, because the controller has to stitch speakers, samples, recording names and segment durations
/// in memory anyway - <see cref="VoiceSample"/> deliberately has no FK to its recording - and the rules are
/// worth testing without a database.</para></summary>
public static class PersonAttributions
{
    /// <summary>Matches the wording <c>PeopleController.Get</c> already uses for an orphaned sample, so the
    /// two lists do not name the same absence differently.</summary>
    private const string DeletedRecording = "(deleted recording)";

    public static IReadOnlyList<PersonAttributionDto> Build(
        IReadOnlyList<AttributionInput> speakers,
        IReadOnlyList<VoiceSample> samples,
        IReadOnlyDictionary<Guid, string> recordingNames,
        IReadOnlySet<Guid> accessibleRecordings,
        IReadOnlySet<Guid> ownedRecordings,
        IReadOnlySet<Guid> recordingsWithAudio)
    {
        // Keyed on the speaker, never the recording: one person can have a speaker in each of two recordings
        // and only one of them enrolled.
        var bySpeaker = samples
            .GroupBy(s => s.SpeakerId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.CreatedAt).First());

        return speakers
            // A speaker that no longer names this person is not a candidate - overlapping audio is a mix of
            // people and can never train a single-person voiceprint, and an unassigned one is the user's own
            // statement that it was not them. It is listed anyway when a sample survives on it, because a
            // sample inside a centroid that nothing on screen accounts for is the defect this closes.
            .Where(s => s.StillLinked || bySpeaker.ContainsKey(s.SpeakerId))
            .Select(s =>
            {
                var sample = bySpeaker.GetValueOrDefault(s.SpeakerId);
                return new PersonAttributionDto(
                    s.SpeakerId,
                    s.RecordingId,
                    recordingNames.TryGetValue(s.RecordingId, out var name) ? name : DeletedRecording,
                    s.Label,
                    s.IdentifiedAuto ? "auto" : "manual",
                    sample is { ExcludedAt: null } && s.StillLinked,
                    sample?.Id,
                    s.SpeechMs,
                    accessibleRecordings.Contains(s.RecordingId),
                    s.StillLinked,
                    ownedRecordings.Contains(s.RecordingId),
                    recordingsWithAudio.Contains(s.RecordingId));
            })
            .OrderBy(r => r.RecordingName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SpeakerLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
