using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests.Llm;

/// <summary>The three states each parameter can be in, and how they compose across layers.
///
/// The distinction between "absent" and "null" is the whole point: with layered defaults, "I have not set
/// this here" and "do not send this at all" are different instructions. A -1 sentinel could not express
/// both, because -1 is a legal, meaningful value for max_tokens (unlimited) and top_k (disabled) on some
/// OpenAI-compatible servers.</summary>
public class LlmParameterLayersTests
{
    private static LlmParameters Resolve(params string?[] layers) => LlmParameterLayers.Resolve(layers);

    [Fact]
    public void A_value_in_the_most_specific_layer_wins()
    {
        var p = Resolve("""{"temperature":0.9}""", """{"temperature":0.3}""");
        Assert.Equal(0.9, p.Temperature);
    }

    [Fact]
    public void An_absent_key_inherits_from_the_next_layer_down()
    {
        var p = Resolve("""{"top_p":0.8}""", """{"temperature":0.3}""");
        Assert.Equal(0.3, p.Temperature);
        Assert.Equal(0.8, p.TopP);
    }

    [Fact]
    public void An_explicit_null_omits_the_parameter_and_stops_the_walk()
    {
        // The case a sentinel cannot express: a lower layer sets 0.3, this layer says send nothing at all.
        var p = Resolve("""{"temperature":null}""", """{"temperature":0.3}""");
        Assert.Null(p.Temperature);
    }

    [Fact]
    public void A_parameter_no_layer_mentions_is_not_sent()
    {
        var p = Resolve("""{"temperature":0.3}""");
        Assert.Null(p.TopK);
        Assert.Null(p.MaxTokens);
        Assert.Null(p.TopP);
    }

    [Fact]
    public void Null_and_empty_layers_are_skipped_rather_than_treated_as_omissions()
    {
        // A model with no override row for a group must inherit, not thereby omit everything.
        var p = Resolve(null, "", "{}", """{"temperature":0.3}""");
        Assert.Equal(0.3, p.Temperature);
    }

    [Fact]
    public void An_unparseable_layer_is_ignored_rather_than_taking_the_platform_down()
    {
        var p = Resolve("not json at all", """{"temperature":0.3}""");
        Assert.Equal(0.3, p.Temperature);
    }

    [Fact]
    public void Behaviour_flags_fall_back_to_their_documented_defaults_when_no_layer_sets_them()
    {
        // These are never "not sent" - they govern the client, so they always need an answer.
        var p = Resolve("{}");
        Assert.False(p.ReasoningEnabled);
        Assert.True(p.ToolsSupported);
        Assert.False(p.ImagesSupported);
        Assert.Equal(LlmParameters.DefaultTimeoutSeconds, p.TimeoutSeconds);
    }

    [Fact]
    public void Behaviour_flags_are_read_from_a_layer_when_one_sets_them()
    {
        var p = Resolve("""{"reasoning_enabled":true,"tools_supported":false,"timeout_seconds":300}""");
        Assert.True(p.ReasoningEnabled);
        Assert.False(p.ToolsSupported);
        Assert.Equal(300, p.TimeoutSeconds);
    }

    [Fact]
    public void Reasoning_effort_is_free_text_so_a_model_specific_level_survives()
    {
        // qwen3 accepts xhigh; gpt-oss does not. An enum here would reject a legitimate value.
        var p = Resolve("""{"reasoning_effort":"xhigh"}""");
        Assert.Equal("xhigh", p.ReasoningEffort);
    }

    [Fact]
    public void Integer_parameters_survive_a_json_integer()
    {
        var p = Resolve("""{"top_k":40,"max_tokens":900,"max_completion_tokens":512}""");
        Assert.Equal(40, p.TopK);
        Assert.Equal(900, p.MaxTokens);
        Assert.Equal(512, p.MaxCompletionTokens);
    }

    [Fact]
    public void Unknown_keys_are_ignored_rather_than_throwing()
    {
        // Forward compatibility: a row written by a newer build must not break an older one on rollback.
        var p = Resolve("""{"temperature":0.3,"nonsense":1}""");
        Assert.Equal(0.3, p.Temperature);
    }

    [Fact]
    public void Names_the_fifteen_parameters_exactly_once_each()
    {
        Assert.Equal(15, LlmParameterLayers.ParameterNames.Count);
        Assert.Equal(LlmParameterLayers.ParameterNames.Count,
            LlmParameterLayers.ParameterNames.Distinct().Count());
        Assert.Contains("temperature", LlmParameterLayers.ParameterNames);
        Assert.Contains("max_completion_tokens", LlmParameterLayers.ParameterNames);
        Assert.Contains("images_supported", LlmParameterLayers.ParameterNames);
        Assert.Contains("ocr_prompt", LlmParameterLayers.ParameterNames);
        Assert.Contains("ocr_max_edge", LlmParameterLayers.ParameterNames);
    }
}
