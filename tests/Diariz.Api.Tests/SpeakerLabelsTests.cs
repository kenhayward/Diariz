using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SpeakerLabelsTests
{
    [Fact]
    public void NextFree_WithNoSpeakers_StartsAtZero() =>
        Assert.Equal("SPEAKER_00", SpeakerLabels.NextFree([]));

    [Fact]
    public void NextFree_IsOnePastTheHighest() =>
        Assert.Equal("SPEAKER_02", SpeakerLabels.NextFree(["SPEAKER_00", "SPEAKER_01"]));

    [Fact]
    public void NextFree_DoesNotFillAGap()
    {
        // A gap exists because a speaker was removed, and a re-transcription may hand that number back
        // out. Reusing it would silently put two different voices under one label.
        Assert.Equal("SPEAKER_03", SpeakerLabels.NextFree(["SPEAKER_00", "SPEAKER_02"]));
    }

    [Fact]
    public void NextFree_IgnoresLabelsThatAreNotNumbered() =>
        Assert.Equal("SPEAKER_01", SpeakerLabels.NextFree(["SPEAKER_00", "UNKNOWN", "Alice", ""]));

    [Fact]
    public void NextFree_PadsToTwoDigitsButDoesNotTruncateBeyond()
    {
        Assert.Equal("SPEAKER_10", SpeakerLabels.NextFree(["SPEAKER_09"]));
        Assert.Equal("SPEAKER_100", SpeakerLabels.NextFree(["SPEAKER_99"]));
    }
}
