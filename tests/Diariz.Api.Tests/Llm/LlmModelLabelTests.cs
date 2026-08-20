using Diariz.Domain.Entities;

namespace Diariz.Api.Tests.Llm;

/// <summary>The display name is what a user picking a model reads; the slug is what the endpoint needs.
/// Blank must fall back rather than render an empty picker row.</summary>
public class LlmModelLabelTests
{
    [Fact]
    public void Falls_back_to_the_slug_when_no_display_name_is_set()
    {
        var model = new LlmModel { Name = "qwen3.8-27b@q4_k_xl" };
        Assert.Equal("qwen3.8-27b@q4_k_xl", model.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_the_slug_for_a_blank_display_name(string blank)
    {
        var model = new LlmModel { Name = "qwen3.8-27b@q4_k_xl", DisplayName = blank };
        Assert.Equal("qwen3.8-27b@q4_k_xl", model.Label);
    }

    [Fact]
    public void Prefers_the_display_name_when_one_is_set()
    {
        var model = new LlmModel { Name = "qwen3.8-27b@q4_k_xl", DisplayName = "QWEN 3.8" };
        Assert.Equal("QWEN 3.8", model.Label);
    }
}
