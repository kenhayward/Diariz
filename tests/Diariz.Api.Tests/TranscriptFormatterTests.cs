using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptFormatterTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto("SPEAKER_00", "Alice", 852, 3896, "So here's the thing."),
        new SegmentDto("SPEAKER_01", "Bob", 64000, 66500, "Right, on the API."),
    ];

    [Fact]
    public void ToPlainText_FormatsSpeakerAndMinuteSecondTimestamp()
    {
        var text = TranscriptFormatter.ToPlainText(Segments);

        Assert.Equal(
            "[00:00] Alice: So here's the thing.\n" +
            "[01:04] Bob: Right, on the API.\n",
            text);
    }

    [Fact]
    public void ToSrt_ProducesNumberedCuesWithMillisecondTimestamps()
    {
        var srt = TranscriptFormatter.ToSrt(Segments);

        Assert.Equal(
            "1\n" +
            "00:00:00,852 --> 00:00:03,896\n" +
            "Alice: So here's the thing.\n" +
            "\n" +
            "2\n" +
            "00:01:04,000 --> 00:01:06,500\n" +
            "Bob: Right, on the API.\n",
            srt);
    }

    [Fact]
    public void ToPlainText_EmptySegments_ReturnsEmptyString()
    {
        Assert.Equal("", TranscriptFormatter.ToPlainText([]));
    }
}
