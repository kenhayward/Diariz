using Diariz.Api.Contracts;
using Diariz.Api.Services.Llm;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests.Llm;

/// <summary>What each client actually puts on the wire.
///
/// Written BEFORE the platform-parameter refactor so it can prove the refactor changed nothing: with an
/// empty database and no environment overrides, every body asserted here must stay identical.
///
/// Nothing asserted these before. The temperature literals - 0.3 in five clients, 0.1 in translation -
/// could have changed in either direction and CI would have stayed green.</summary>
public class RequestBodyCharacterisationTests
{
    /// <summary>A response shaped like a chat completion whose content is an empty JSON array, which is
    /// what the extraction clients (actions, tags, translation) try to parse.</summary>
    private const string ChatResponse =
        """{"choices":[{"message":{"content":"[]"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    /// <summary>Every parameter that must NOT appear in a request today. Asserting absence is the point:
    /// no token cap is sent, so output length is entirely the server's choice, and if a change starts
    /// sending one that must be a deliberate edit here rather than a silent drift.</summary>
    private static readonly string[] NeverSentToday =
    [
        "max_tokens", "max_completion_tokens", "top_p", "top_k",
        "repeat_penalty", "frequency_penalty", "presence_penalty",
    ];

    private static LlmRequestConfig Config() =>
        new("http://llm.test/v1", "k", "test-model", new LlmParameters { TimeoutSeconds = 120 });

    /// <summary>The same config with reasoning turned on. Both halves are needed: an effort with reasoning
    /// off is deliberately not sent, which is what the paired omission test above pins.</summary>
    private static LlmRequestConfig WithReasoning(string effort) =>
        Config() with
        {
            Parameters = Config().Parameters with { ReasoningEnabled = true, ReasoningEffort = effort },
        };

    private static IReadOnlyList<SegmentDto> Segments() =>
        [new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alex", 0, 1000, "hello")];

    /// <summary>Runs a client call and returns the captured handler.
    ///
    /// Response-parsing failures are swallowed deliberately: these tests are about the REQUEST, and each
    /// client parses its response differently, so satisfying five parsers would couple this file to
    /// behaviour it is not testing. Swallowing is safe because `LastBody` throws when no request was
    /// captured - a client that never sent anything still fails loudly.</summary>
    private static async Task<CapturingHandler> Capture(Func<HttpClient, Task> call)
    {
        var handler = new CapturingHandler(ChatResponse);
        try
        {
            await call(new HttpClient(handler));
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            // Response shape is irrelevant here.
        }
        return handler;
    }

    private static void AssertOnlyTodaysParameters(CapturingHandler handler, double expectedTemperature)
    {
        var body = handler.LastBody;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(expectedTemperature, body.GetProperty("temperature").GetDouble(), 3);
        Assert.True(body.TryGetProperty("messages", out _), "no messages in the request body");

        foreach (var absent in NeverSentToday)
            Assert.False(body.TryGetProperty(absent, out _), $"unexpected {absent} in the request body");
    }

    [Fact]
    public async Task Summarization_sends_model_temperature_and_messages_only()
    {
        var handler = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(Config(), Segments(), needName: false, template: "T"));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task MeetingMinutes_sends_the_same_temperature_as_summarisation()
    {
        var handler = await Capture(h =>
            new MeetingMinutesClient(h).GenerateAsync(Config(), [new ChatMessage("user", "make minutes")]));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task Actions_sends_the_same_temperature_as_summarisation()
    {
        var handler = await Capture(h =>
            new ActionsClient(h).ExtractAsync(Config(), Segments(), "T", meetingDate: null));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task Tags_sends_the_same_temperature_as_summarisation()
    {
        var handler = await Capture(h => new TagsClient(h).ExtractAsync(Config(), Segments(), "T"));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task Translation_runs_cooler_than_everything_else()
    {
        // The one deliberate exception in the codebase today, and the reason app defaults have to be
        // group-capable rather than a single flat set.
        var handler = await Capture(h => new TranslationClient(h).TranslateAsync(Config(), "English", ["hola"]));

        AssertOnlyTodaysParameters(handler, 0.1);
    }

    [Fact]
    public async Task Chat_stream_sends_stream_true_and_asks_for_usage()
    {
        var handler = await Capture(async h =>
        {
            await foreach (var _ in new ChatStreamClient(h).StreamAsync(Config(), [new ChatMessage("user", "hi")]))
            {
            }
        });

        var body = handler.LastBody;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(0.3, body.GetProperty("temperature").GetDouble(), 3);
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        foreach (var absent in NeverSentToday)
            Assert.False(body.TryGetProperty(absent, out _), $"unexpected {absent} in the request body");
    }

    [Fact]
    public async Task Chat_stream_chunks_sends_tools_when_given_them()
    {
        var tools = new List<object> { new { type = "function", function = new { name = "noop" } } };
        var handler = await Capture(async h =>
        {
            await foreach (var _ in new ChatStreamClient(h)
                               .StreamChunksAsync(Config(), [new { role = "user", content = "hi" }], tools))
            {
            }
        });

        var body = handler.LastBody;
        Assert.Equal(0.3, body.GetProperty("temperature").GetDouble(), 3);
        Assert.True(body.TryGetProperty("tools", out _));
        Assert.Equal("auto", body.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task Reasoning_effort_is_sent_only_when_the_config_supplies_one()
    {
        var without = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(Config(), Segments(), false, "T"));
        Assert.False(without.Has("reasoning_effort"));

        var with = await Capture(h => new SummarizationClient(h)
            .SummarizeAsync(WithReasoning("high"), Segments(), false, "T"));
        Assert.Equal("high", with.LastBody.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Every_client_posts_to_chat_completions_on_the_configured_base()
    {
        var handler = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(Config(), Segments(), false, "T"));

        Assert.Equal("http://llm.test/v1/chat/completions", handler.LastRequestUri!.ToString());
    }
}
