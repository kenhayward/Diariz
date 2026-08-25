using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>What counts as training data for a voiceprint.
///
/// <para>The rule exists because the two assignment paths both moved a speaker's link and left the voice
/// sample behind: on the live instance six samples were training a person whose transcript named someone
/// else, three of them a specifically different person. Stored state would need every assignment path to
/// remember to update it - a rule needs none of them to.</para></summary>
public class VoiceprintTrainingTests
{
    private static readonly Guid Person = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static VoiceSample Sample(DateTimeOffset? excludedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        PersonId = Person,
        SpeakerId = Guid.NewGuid(),
        RecordingId = Guid.NewGuid(),
        ExcludedAt = excludedAt,
    };

    private static Speaker Speaker(Guid? personId, bool multi = false) => new()
    {
        Id = Guid.NewGuid(),
        RecordingId = Guid.NewGuid(),
        Label = "SPEAKER_00",
        DisplayName = "SPEAKER_00",
        PersonId = personId,
        IsMultiSpeaker = multi,
    };

    [Fact]
    public void A_linked_speaker_trains_the_voiceprint()
    {
        Assert.True(VoiceprintTraining.Trains(Sample(), Speaker(Person)));
    }

    [Fact]
    public void A_sample_dropped_by_hand_does_not_train()
    {
        Assert.False(VoiceprintTraining.Trains(Sample(DateTimeOffset.UtcNow), Speaker(Person)));
    }

    [Fact]
    public void An_unlinked_speaker_does_not_train()
    {
        // Unassigning a speaker on the transcript is the user saying "that was not them". The sample it
        // enrolled kept training regardless, which is the defect this rule closes.
        Assert.False(VoiceprintTraining.Trains(Sample(), Speaker(null)));
    }

    [Fact]
    public void A_speaker_now_attributed_to_someone_else_does_not_train()
    {
        // The worst of the six found live: person A's voiceprint learning from audio the user has since
        // labelled as person B. Both people are then taught the same voice.
        Assert.False(VoiceprintTraining.Trains(Sample(), Speaker(Other)));
    }

    [Fact]
    public void Overlapping_speech_does_not_train()
    {
        Assert.False(VoiceprintTraining.Trains(Sample(), Speaker(Person, multi: true)));
    }

    [Fact]
    public void A_missing_speaker_does_not_train()
    {
        // Defensive: the FK cascades, so this should be unreachable. If it ever is reachable, a null speaker
        // must mean "no evidence", never "assume it still counts".
        Assert.False(VoiceprintTraining.Trains(Sample(), null));
    }

    [Fact]
    public void Still_linked_reads_the_speaker_not_the_sample()
    {
        // The overload the attributions endpoint uses, where only the projected columns are in hand.
        Assert.True(VoiceprintTraining.StillLinked(Person, Person, false));
        Assert.False(VoiceprintTraining.StillLinked(Person, Other, false));
        Assert.False(VoiceprintTraining.StillLinked(Person, null, false));
        Assert.False(VoiceprintTraining.StillLinked(Person, Person, true));
    }
}
