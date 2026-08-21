using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services.Llm;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

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

    /// <summary>What the REAL resolver produces for this call kind against an empty database and the shipped
    /// app defaults - which is exactly the configuration a deployment has before an administrator visits
    /// /admin/llm-models.
    ///
    /// It resolves rather than hand-building a config on purpose. Before 0.221.0 the temperature was a
    /// literal inside each client, so a hand-built config was enough to pin it. Now the client sends what it
    /// is given, and the value comes from LlmDefaultsOptions - so a hand-built config with no parameters
    /// would assert that the clients send nothing, which is true but proves nothing about production. Going
    /// through the resolver keeps these tests pinning the bodies a real request carries.</summary>
    private static async Task<LlmRequestConfig> Config(LlmCallKind kind = LlmCallKind.Summarize)
    {
        using var db = TestDb.Create();
        var resolver = new LlmSettingsResolver(
            db,
            Options.Create(new LlmDefaultsOptions()),
            Options.Create(new SummarizationOptions
            {
                ApiBase = "http://llm.test/v1", ApiKey = "k", Model = "test-model",
            }),
            new FakeApiKeyProtector(),
            new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())),
            Options.Create(new ChatOptions()));

        return await resolver.ResolveAsync(kind);
    }

    /// <summary>The same config with reasoning turned on. Both halves are needed: an effort with reasoning
    /// off is deliberately not sent, which is what the paired omission test above pins.</summary>
    private static async Task<LlmRequestConfig> WithReasoning(string effort)
    {
        var config = await Config();
        return config with
        {
            Parameters = config.Parameters with { ReasoningEnabled = true, ReasoningEffort = effort },
        };
    }

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
        var config = await Config();
        var handler = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(config, Segments(), needName: false, template: "T"));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task MeetingMinutes_sends_the_same_temperature_as_summarisation()
    {
        var config = await Config(LlmCallKind.MeetingMinutes);
        var handler = await Capture(h =>
            new MeetingMinutesClient(h).GenerateAsync(config, [new ChatMessage("user", "make minutes")]));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task Actions_sends_the_same_temperature_as_summarisation()
    {
        var config = await Config(LlmCallKind.ExtractActions);
        var handler = await Capture(h =>
            new ActionsClient(h).ExtractAsync(config, Segments(), "T", meetingDate: null));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task Tags_sends_the_same_temperature_as_summarisation()
    {
        var config = await Config(LlmCallKind.Tags);
        var handler = await Capture(h => new TagsClient(h).ExtractAsync(config, Segments(), "T"));

        AssertOnlyTodaysParameters(handler, 0.3);
    }

    [Fact]
    public async Task Translation_runs_cooler_than_everything_else()
    {
        // The one deliberate exception in the codebase today, and the reason app defaults have to be
        // group-capable rather than a single flat set.
        var config = await Config(LlmCallKind.Translation);
        var handler = await Capture(h => new TranslationClient(h).TranslateAsync(config, "English", ["hola"]));

        AssertOnlyTodaysParameters(handler, 0.1);
    }

    [Fact]
    public async Task Chat_stream_sends_stream_true_and_asks_for_usage()
    {
        var config = await Config(LlmCallKind.ChatMessage);
        var handler = await Capture(async h =>
        {
            await foreach (var _ in new ChatStreamClient(h).StreamAsync(config, [new ChatMessage("user", "hi")]))
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
        var config = await Config(LlmCallKind.ChatMessage);
        var tools = new List<object> { new { type = "function", function = new { name = "noop" } } };
        var handler = await Capture(async h =>
        {
            await foreach (var _ in new ChatStreamClient(h)
                               .StreamChunksAsync(config, [new { role = "user", content = "hi" }], tools))
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
        var config = await Config();
        var without = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(config, Segments(), false, "T"));
        Assert.False(without.Has("reasoning_effort"));

        var reasoning = await WithReasoning("high");
        var with = await Capture(h => new SummarizationClient(h)
            .SummarizeAsync(reasoning, Segments(), false, "T"));
        Assert.Equal("high", with.LastBody.GetProperty("reasoning_effort").GetString());
    }

    // ---- What the clients do with a resolved parameter set (0.221.0) ----

    [Fact]
    public async Task Sends_the_parameters_the_resolver_decided()
    {
        var config = await Config() with
        {
            Parameters = new LlmParameters { Temperature = 0.75, MaxTokens = 900, TopK = 40 },
        };

        var handler = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(config, Segments(), false, "T"));

        var body = handler.LastBody;
        Assert.Equal(0.75, body.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(900, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(40, body.GetProperty("top_k").GetInt32());
    }

    [Fact]
    public async Task Omits_a_parameter_the_resolver_decided_not_to_send()
    {
        // Null means "leave the key out", not "send null" - a server that validates types 400s on
        // "temperature": null.
        var config = await Config() with { Parameters = new LlmParameters { Temperature = null, TopP = 0.9 } };

        var handler = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(config, Segments(), false, "T"));

        Assert.False(handler.Has("temperature"));
        Assert.Equal(0.9, handler.LastBody.GetProperty("top_p").GetDouble(), 3);
    }

    [Fact]
    public async Task Chat_omits_tools_when_the_model_does_not_support_them()
    {
        var config = await Config() with { Parameters = new LlmParameters { ToolsSupported = false } };
        var tools = new List<object> { new { type = "function", function = new { name = "noop" } } };

        var handler = await Capture(async h =>
        {
            await foreach (var _ in new ChatStreamClient(h)
                               .StreamChunksAsync(config, [new { role = "user", content = "hi" }], tools))
            {
            }
        });

        Assert.False(handler.Has("tools"));
        Assert.False(handler.Has("tool_choice"));
    }

    [Fact]
    public async Task Every_client_posts_to_chat_completions_on_the_configured_base()
    {
        var config = await Config();
        var handler = await Capture(h =>
            new SummarizationClient(h).SummarizeAsync(config, Segments(), false, "T"));

        Assert.Equal("http://llm.test/v1/chat/completions", handler.LastRequestUri!.ToString());
    }
}
