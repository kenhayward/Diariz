using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests.Llm;

public class LlmRequestBodyTests
{
    private static Dictionary<string, object?> Apply(LlmParameters p)
    {
        var body = new Dictionary<string, object?> { ["model"] = "m" };
        LlmRequestBody.Apply(body, p);
        return body;
    }

    [Fact]
    public void Writes_each_wire_parameter_that_has_a_value()
    {
        var body = Apply(new LlmParameters
        {
            Temperature = 0.3, TopP = 0.9, TopK = 40, RepeatPenalty = 1.1,
            FrequencyPenalty = 0.2, PresencePenalty = 0.1, MaxTokens = 512, MaxCompletionTokens = 256,
        });

        Assert.Equal(0.3, body["temperature"]);
        Assert.Equal(0.9, body["top_p"]);
        Assert.Equal(40, body["top_k"]);
        Assert.Equal(1.1, body["repeat_penalty"]);
        Assert.Equal(0.2, body["frequency_penalty"]);
        Assert.Equal(0.1, body["presence_penalty"]);
        Assert.Equal(512, body["max_tokens"]);
        Assert.Equal(256, body["max_completion_tokens"]);
    }

    [Fact]
    public void Omits_a_null_parameter_entirely_rather_than_writing_a_json_null()
    {
        // Not a stylistic choice: sending "top_k": null to a server that validates types is a 400.
        var body = Apply(new LlmParameters { Temperature = 0.3 });

        Assert.False(body.ContainsKey("top_k"));
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body.ContainsKey("top_p"));
        Assert.False(body.ContainsKey("repeat_penalty"));
    }

    [Fact]
    public void Sends_reasoning_effort_only_when_reasoning_is_enabled()
    {
        Assert.False(Apply(new LlmParameters { ReasoningEnabled = false, ReasoningEffort = "high" })
            .ContainsKey("reasoning_effort"));

        Assert.Equal("high", Apply(new LlmParameters { ReasoningEnabled = true, ReasoningEffort = "high" })
            ["reasoning_effort"]);
    }

    [Fact]
    public void Sends_no_reasoning_effort_when_enabled_but_no_level_was_decided()
    {
        Assert.False(Apply(new LlmParameters { ReasoningEnabled = true, ReasoningEffort = null })
            .ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void Never_writes_a_behaviour_flag_into_the_body()
    {
        // Timeout, tool support and image support govern the client. An endpoint receiving them would at
        // best ignore them and at worst reject the request.
        var body = Apply(new LlmParameters
        {
            TimeoutSeconds = 300, ToolsSupported = true, ImagesSupported = true, ReasoningEnabled = true,
        });

        foreach (var key in new[] { "timeout_seconds", "tools_supported", "images_supported", "reasoning_enabled" })
            Assert.False(body.ContainsKey(key), $"{key} must never reach the wire");
    }

    [Fact]
    public void Leaves_keys_the_caller_already_set_alone()
    {
        var body = new Dictionary<string, object?> { ["model"] = "m", ["messages"] = new object() };
        LlmRequestBody.Apply(body, new LlmParameters { Temperature = 0.3 });

        Assert.Equal("m", body["model"]);
        Assert.True(body.ContainsKey("messages"));
    }
}
