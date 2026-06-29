using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class SegmentTests
{
    [Fact]
    public void EffectiveText_IsOriginal_WhenNotRevised()
    {
        var s = new Segment { Original = "model output" };

        Assert.Null(s.Revised);
        Assert.Equal("model output", s.EffectiveText);
    }

    [Fact]
    public void EffectiveText_PrefersRevised_WhenSet()
    {
        var s = new Segment { Original = "model output", Revised = "my edit" };

        Assert.Equal("my edit", s.EffectiveText);
    }

    [Fact]
    public void EffectiveText_UsesRevised_EvenWhenBlank()
    {
        // A deliberate blank revision ("") is distinct from "no revision" (null) — it shows blank.
        var s = new Segment { Original = "model output", Revised = "" };

        Assert.Equal("", s.EffectiveText);
    }
}
