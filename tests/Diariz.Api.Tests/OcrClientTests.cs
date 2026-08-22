using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

/// <summary>What the OCR client actually puts on the wire.
///
/// Every assertion here is a measured requirement rather than a preference. An OCR model is not a chat
/// model: it gets one turn, no system message, no history and no tools, and llama.cpp's OCR guidance puts
/// the image part BEFORE the text part. Chat's own shaping (ChatToolOrchestrator.Shape) does the opposite,
/// correctly for chat, which is exactly why this client builds its own body.</summary>
public class OcrClientTests
{
    private const string Reply = """
        {"choices":[{"message":{"content":"Extracted text"}}]}
        """;

    private static LlmRequestConfig Config(LlmParameters? p = null, string key = "") =>
        new("http://lmstudio.local/v1", key, "olmocr-2-7b-1025", p ?? new LlmParameters
        {
            Temperature = 0.02,
            OcrPrompt = "Text Recognition:",
            OcrMaxEdge = 2048,
        });

    private static (OcrClient Client, CapturingHandler Handler) Build(string reply = Reply)
    {
        var handler = new CapturingHandler(reply);
        return (new OcrClient(new HttpClient(handler)), handler);
    }

    private static JsonElement UserContent(CapturingHandler h) =>
        h.LastBody.GetProperty("messages")[0].GetProperty("content");

    [Fact]
    public async Task Posts_to_chat_completions_on_the_configured_base()
    {
        var (client, handler) = Build();
        await client.ExtractAsync(Config(), "data:image/png;base64,AAAA");

        Assert.Equal("http://lmstudio.local/v1/chat/completions", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task Returns_the_message_content()
    {
        var (client, _) = Build();
        Assert.Equal("Extracted text", await client.ExtractAsync(Config(), "data:image/png;base64,AAAA"));
    }

    /// <summary>One turn, one message. A system prompt or a history would be sent to a model whose own card
    /// says it does not do multi-turn conversation.</summary>
    [Fact]
    public async Task Sends_exactly_one_user_message_and_no_system_message()
    {
        var (client, handler) = Build();
        await client.ExtractAsync(Config(), "data:image/png;base64,AAAA");

        var messages = handler.LastBody.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    /// <summary>Image first, then the text - llama.cpp's documented ordering for OCR models.</summary>
    [Fact]
    public async Task Sends_the_image_part_before_the_text_part()
    {
        var (client, handler) = Build();
        await client.ExtractAsync(Config(), "data:image/png;base64,AAAA");

        var content = UserContent(handler);
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("image_url", content[0].GetProperty("type").GetString());
        Assert.Equal(
            "data:image/png;base64,AAAA",
            content[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
    }

    /// <summary>The prompt is per-model and reaches the wire verbatim: GLM-OCR wants "Text Recognition:",
    /// olmOCR wants a sentence, and a general VL model wants something else again. Reworded or wrapped, it
    /// would stop being the string the administrator measured.</summary>
    [Fact]
    public async Task Sends_the_configured_prompt_verbatim()
    {
        var (client, handler) = Build();
        await client.ExtractAsync(
            Config(new LlmParameters { OcrPrompt = "Just return the plain text.", OcrMaxEdge = 2048 }),
            "data:image/png;base64,AAAA");

        Assert.Equal("Just return the plain text.", UserContent(handler)[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Never_offers_tools_however_the_parameters_are_set()
    {
        var (client, handler) = Build();
        await client.ExtractAsync(
            Config(new LlmParameters { ToolsSupported = true, OcrPrompt = "x", OcrMaxEdge = 2048 }),
            "data:image/png;base64,AAAA");

        Assert.False(handler.Has("tools"));
        Assert.False(handler.Has("tool_choice"));
    }

    /// <summary>Non-streaming: there is no partial answer worth showing for a single OCR pass, and the
    /// caller wants one string.</summary>
    [Fact]
    public async Task Does_not_stream()
    {
        var (client, handler) = Build();
        await client.ExtractAsync(Config(), "data:image/png;base64,AAAA");

        Assert.False(handler.Has("stream") && handler.LastBody.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Applies_the_resolved_sampling_parameters()
    {
        var (client, handler) = Build();
        await client.ExtractAsync(
            Config(new LlmParameters { Temperature = 0.02, TopK = 1, OcrPrompt = "x", OcrMaxEdge = 2048 }),
            "data:image/png;base64,AAAA");

        Assert.Equal(0.02, handler.LastBody.GetProperty("temperature").GetDouble());
        Assert.Equal(1, handler.LastBody.GetProperty("top_k").GetInt32());
    }

    /// <summary>A local LM Studio endpoint needs no key, and sending an empty Bearer is worse than sending
    /// nothing - some servers reject it.</summary>
    [Fact]
    public async Task Blank_response_content_comes_back_as_empty_rather_than_throwing()
    {
        var (client, _) = Build("""{"choices":[]}""");
        Assert.Equal("", await client.ExtractAsync(Config(), "data:image/png;base64,AAAA"));
    }
}
