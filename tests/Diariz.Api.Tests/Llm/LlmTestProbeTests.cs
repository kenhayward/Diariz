using System.Net;
using System.Text.Json;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests.Llm;

/// <summary>The administrator's "does this actually work" call.
///
/// It is the only LLM call in the platform whose RESULT is shown to a person rather than stored, so it has
/// to report the things a person needs to act on: what came back, how long the model took to say anything,
/// and - when it failed - which of their parameters caused it.</summary>
public class LlmTestProbeTests
{
    private static LlmRequestConfig Config(LlmParameters? p = null, int timeoutSeconds = 30) =>
        new("http://llm/v1", "sk-secret", "test-model",
            (p ?? new LlmParameters()) with { TimeoutSeconds = timeoutSeconds });

    private static string Delta(string text) =>
        JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });

    private static LlmTestProbe Probe(HttpMessageHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task Returns_what_the_model_actually_said()
    {
        var handler = new SseHandler([Delta("The team "), Delta("agreed."), "[DONE]"]);

        var result = await Probe(handler).RunAsync(Config(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("The team agreed.", result.Response);
    }

    [Fact]
    public async Task Asks_for_a_stream_and_for_its_token_counts()
    {
        // Time-to-first-token only exists on a stream, and it is the number the result leads with.
        var handler = new SseHandler([Delta("hi"), "[DONE]"]);

        await Probe(handler).RunAsync(Config(), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastBodyRaw!).RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("http://llm/v1/chat/completions", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task Sends_the_resolved_parameters_and_not_the_behaviour_flags()
    {
        // Exactly what LlmRequestBody.Apply writes - the drawer's preview claims to show this body, so a
        // probe that sent something else would make the preview a lie in the one place it is checkable.
        var handler = new SseHandler([Delta("hi"), "[DONE]"]);
        var parameters = new LlmParameters { Temperature = 0.25, TopK = 20, TimeoutSeconds = 600, ToolsSupported = false };

        await Probe(handler).RunAsync(Config(parameters), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastBodyRaw!).RootElement;
        Assert.Equal(0.25, body.GetProperty("temperature").GetDouble());
        Assert.Equal(20, body.GetProperty("top_k").GetInt32());
        Assert.False(body.TryGetProperty("timeout_seconds", out _));
        Assert.False(body.TryGetProperty("tools_supported", out _));
    }

    [Fact]
    public async Task Authenticates_with_the_stored_key()
    {
        var handler = new SseHandler([Delta("hi"), "[DONE]"]);

        await Probe(handler).RunAsync(Config(), CancellationToken.None);

        Assert.Equal("Bearer sk-secret", handler.LastAuthorization);
    }

    [Fact]
    public async Task Reports_the_token_counts_the_endpoint_sent()
    {
        var usage = JsonSerializer.Serialize(new
        {
            choices = Array.Empty<object>(),
            usage = new
            {
                prompt_tokens = 1240,
                completion_tokens = 44,
                total_tokens = 1412,
                completion_tokens_details = new { reasoning_tokens = 128 },
            },
        });
        var handler = new SseHandler([Delta("hi"), usage, "[DONE]"]);

        var result = await Probe(handler).RunAsync(Config(), CancellationToken.None);

        Assert.Equal(1240, result.PromptTokens);
        Assert.Equal(44, result.CompletionTokens);
        Assert.Equal(128, result.ReasoningTokens);
        Assert.Equal(1412, result.TotalTokens);
    }

    [Fact]
    public async Task Measures_the_first_token_separately_from_the_whole_call()
    {
        // The one assertion that a buffering implementation cannot pass. If the probe read the body to the
        // end before timing anything, first-token time would equal total duration; here the endpoint holds
        // the last chunk back, so the two have to differ by roughly that hold.
        var handler = new SseHandler(
            [Delta("first"), Delta("last"), "[DONE]"],
            delayBefore: TimeSpan.FromMilliseconds(400), delayAtIndex: 1);

        var result = await Probe(handler).RunAsync(Config(), CancellationToken.None);

        Assert.NotNull(result.TtftMs);
        Assert.True(
            result.DurationMs - result.TtftMs >= 150,
            $"first token {result.TtftMs}ms vs duration {result.DurationMs}ms - the body was buffered");
    }

    [Fact]
    public async Task Reports_a_timeout_as_a_timeout_rather_than_a_crash()
    {
        var handler = new SseHandler(
            [Delta("hi"), "[DONE]"], delayBefore: TimeSpan.FromSeconds(30), delayAtIndex: 0);

        var result = await Probe(handler).RunAsync(Config(timeoutSeconds: 1), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Timeout", result.ErrorKind);
    }

    [Fact]
    public async Task Names_the_parameter_a_rejection_blamed()
    {
        // This is what the one-click fix acts on: without it a 400 is an error message the admin has to
        // read, decode, and then know that omitting a parameter is even possible.
        var handler = new SseHandler(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"Unrecognized request argument supplied: top_k"}}""");

        var result = await Probe(handler).RunAsync(Config(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(400, result.HttpStatus);
        Assert.Equal("Http400", result.ErrorKind);
        Assert.Equal("top_k", result.OffendingParameter);
        Assert.Contains("top_k", result.Message);
    }

    [Fact]
    public async Task Reports_a_transport_failure_without_throwing()
    {
        // A wrong endpoint is the single most likely thing an admin is testing FOR, so it has to come back
        // as a result they can read rather than a 500 from the API.
        var result = await Probe(new ThrowingHandler()).RunAsync(Config(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Transport", result.ErrorKind);
    }

    [Fact]
    public async Task Reports_the_reason_the_model_stopped()
    {
        var stop = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "length" } } });
        var handler = new SseHandler([Delta("hi"), stop, "[DONE]"]);

        var result = await Probe(handler).RunAsync(Config(), CancellationToken.None);

        Assert.Equal("length", result.FinishReason);
    }

    [Fact]
    public async Task Hands_back_the_body_it_sent_so_the_result_can_be_reproduced()
    {
        // "Copy as cURL" is only trustworthy if it quotes the request that actually ran, not one rebuilt
        // from the editor's state afterwards.
        var handler = new SseHandler([Delta("hi"), "[DONE]"]);

        var result = await Probe(handler).RunAsync(Config(new LlmParameters { Temperature = 0.25 }), CancellationToken.None);

        var sent = JsonDocument.Parse(handler.LastBodyRaw!).RootElement;
        var reported = JsonDocument.Parse(result.RequestBodyJson).RootElement;
        Assert.Equal(sent.GetProperty("temperature").GetDouble(), reported.GetProperty("temperature").GetDouble());
        Assert.Equal("test-model", reported.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Never_reports_the_api_key_in_the_request_it_hands_back()
    {
        // The result goes to a browser. The key is write-only everywhere else and must stay that way here.
        var handler = new SseHandler([Delta("hi"), "[DONE]"]);

        var result = await Probe(handler).RunAsync(Config(), CancellationToken.None);

        Assert.DoesNotContain("sk-secret", result.RequestBodyJson);
        // ...and the body really is the one it sent: an absence assertion alone passes against "{}".
        Assert.Contains("test-model", result.RequestBodyJson);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) =>
            throw new HttpRequestException("No connection could be made");
    }
}
