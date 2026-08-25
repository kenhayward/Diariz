using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>What counts as training data for a voiceprint.
///
/// <para><b>A sample trains a person's voiceprint only while its speaker still says it is that person.</b>
/// Both assignment paths - <c>SpeakerAssignment.Unassign</c> and <c>AssignAsync</c> - move the speaker's link
/// and leave any existing <see cref="VoiceSample"/> untouched, so the alternative is a stored flag that every
/// present and future assignment path has to remember to update. A rule needs none of them to, and heals the
/// rows that are already wrong.</para>
///
/// <para>Pure and shared rather than repeated at each call site: the failure this fixes is precisely two
/// surfaces disagreeing about which samples count. The Voiceprint tab listed linked speakers while the
/// Diagnostics tab listed samples, so a sample whose speaker had been unlinked was diagnosed as an outlier
/// on a screen that offered no way to reach it.</para></summary>
public static class VoiceprintTraining
{
    /// <summary>Whether the speaker behind a sample still attributes it to this person. Takes the projected
    /// columns rather than a <see cref="Speaker"/>, because the attributions endpoint reads them straight out
    /// of a projection and never materialises the entity.</summary>
    public static bool StillLinked(Guid personId, Guid? speakerPersonId, bool speakerIsMultiSpeaker) =>
        speakerPersonId == personId && !speakerIsMultiSpeaker;

    /// <summary>Whether this sample currently trains its person's voiceprint. A null
    /// <paramref name="speaker"/> is no evidence, never an assumption that it still counts.</summary>
    public static bool Trains(VoiceSample sample, Speaker? speaker) =>
        sample.ExcludedAt is null
        && speaker is not null
        && StillLinked(sample.PersonId, speaker.PersonId, speaker.IsMultiSpeaker);
}
