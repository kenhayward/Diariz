using Diariz.Api.Services;
using static Diariz.Api.Services.SegmentMerger;

namespace Diariz.Api.Tests;

public class SegmentMergerTests
{
    [Fact]
    public void Merge_CollapsesConsecutiveSameSpeaker_JoinsText_SpansTimestamps()
    {
        var parts = new List<Part>
        {
            new("SPEAKER_00", 0, 1000, "Hello"),
            new("SPEAKER_00", 1000, 2000, "there"),
            new("SPEAKER_01", 2000, 3000, "Hi"),
            new("SPEAKER_00", 3000, 4000, "Bye"),
        };

        var merged = Merge(parts);

        Assert.Equal(3, merged.Count);
        Assert.Equal(new Part("SPEAKER_00", 0, 2000, "Hello there"), merged[0]);
        Assert.Equal(new Part("SPEAKER_01", 2000, 3000, "Hi"), merged[1]);
        Assert.Equal(new Part("SPEAKER_00", 3000, 4000, "Bye"), merged[2]);
    }

    [Fact]
    public void Merge_AllSameSpeaker_ProducesOnePart()
    {
        var parts = new List<Part>
        {
            new("S", 0, 1000, "a"),
            new("S", 1000, 2000, "b"),
            new("S", 2000, 3000, "c"),
        };

        var merged = Merge(parts);

        Assert.Single(merged);
        Assert.Equal(new Part("S", 0, 3000, "a b c"), merged[0]);
    }

    [Fact]
    public void Merge_AllDifferentSpeakers_IsUnchanged()
    {
        var parts = new List<Part>
        {
            new("A", 0, 1000, "x"),
            new("B", 1000, 2000, "y"),
        };

        Assert.Equal(parts, Merge(parts));
    }

    [Fact]
    public void Merge_SkipsBlankTextWithoutDoubleSpacing()
    {
        var parts = new List<Part>
        {
            new("S", 0, 1000, "hello"),
            new("S", 1000, 2000, ""),
            new("S", 2000, 3000, "world"),
        };

        Assert.Equal("hello world", Merge(parts).Single().Text);
    }

    [Fact]
    public void Merge_Empty_ReturnsEmpty() => Assert.Empty(Merge([]));
}
