using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>How much each speaker actually says, which the minimum-speech gate reads and the Voiceprint tab
/// displays. One implementation on purpose: a gate that disagreed with the figure shown next to it would be
/// impossible to explain to whoever is looking at both.</summary>
public class SpeakerSpeechTests
{
    [Fact]
    public void FromSegments_sums_each_speakers_own_segments()
    {
        var speech = SpeakerSpeech.FromSegments([
            ("SPEAKER_00", 0, 1000),
            ("SPEAKER_01", 1000, 4000),
            ("SPEAKER_00", 4000, 4500),
        ]);

        Assert.Equal(1500, speech["SPEAKER_00"]);
        Assert.Equal(3000, speech["SPEAKER_01"]);
    }

    [Fact]
    public void FromSegments_ignores_a_negative_length()
    {
        // Defensive: a reversed span would otherwise subtract from the total and could pull a speaker under
        // the gate, which reads as "too little speech" rather than as the bad data it is.
        var speech = SpeakerSpeech.FromSegments([("SPEAKER_00", 5000, 1000), ("SPEAKER_00", 0, 2000)]);

        Assert.Equal(2000, speech["SPEAKER_00"]);
    }

    [Fact]
    public void FromSegments_skips_a_blank_label()
    {
        var speech = SpeakerSpeech.FromSegments([("", 0, 1000), ("   ", 0, 1000), ("SPEAKER_00", 0, 1000)]);

        Assert.Single(speech);
        Assert.Equal(1000, speech["SPEAKER_00"]);
    }

    [Fact]
    public void FromSegments_of_nothing_is_empty()
    {
        Assert.Empty(SpeakerSpeech.FromSegments([]));
    }

    [Fact]
    public void MsFor_treats_an_unknown_label_as_silence()
    {
        // A speaker row with no segments in the current transcription has said nothing measurable, and must
        // read as zero rather than throwing in the middle of identifying a recording.
        Assert.Equal(0, SpeakerSpeech.MsFor(new Dictionary<string, long>(), "SPEAKER_09"));
    }
}
