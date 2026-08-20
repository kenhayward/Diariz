using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class SegmentMapperTests
{
    private static Segment Seg(int ordinal, string label, string text) => new()
    {
        Id = Guid.NewGuid(), Ordinal = ordinal, SpeakerLabel = label,
        StartMs = ordinal * 1000, EndMs = ordinal * 1000 + 900, Original = text,
    };

    [Fact]
    public void Orders_by_ordinal_and_resolves_display_names()
    {
        var segments = new[] { Seg(1, "SPEAKER_01", "second"), Seg(0, "SPEAKER_00", "first") };
        var names = new Dictionary<string, string> { ["SPEAKER_00"] = "Priya" };

        var dtos = SegmentMapper.ToDtos(segments, names);

        Assert.Equal(["first", "second"], dtos.Select(d => d.Original));
        Assert.Equal("Priya", dtos[0].SpeakerDisplay);
    }

    [Fact]
    public void Falls_back_to_the_raw_label_for_an_unnamed_speaker()
    {
        // A recording whose speakers were never renamed must still produce a usable transcript, not blanks.
        var dtos = SegmentMapper.ToDtos([Seg(0, "SPEAKER_02", "hello")], new Dictionary<string, string>());

        Assert.Equal("SPEAKER_02", Assert.Single(dtos).SpeakerDisplay);
    }

    [Fact]
    public void Prefers_the_revised_text_the_way_the_pipeline_does()
    {
        var seg = Seg(0, "SPEAKER_00", "orignal typo");
        seg.Revised = "original typo";

        Assert.Equal("original typo", Assert.Single(SegmentMapper.ToDtos([seg], new Dictionary<string, string>())).Text);
    }
}
