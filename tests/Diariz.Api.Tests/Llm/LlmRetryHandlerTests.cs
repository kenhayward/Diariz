using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests.Llm;

/// <summary>The retry policy for unattended model calls.
///
/// <para>The delay is injected throughout, so these run instantly and still exercise the real backoff
/// decision rather than a shortened copy of it. The recorded delays are asserted directly.</para></summary>
public class LlmRetryHandlerTests
{
    private static readonly Func<HttpResponseMessage> Ok =
        ScriptedHandler.Json(HttpStatusCode.OK, """{"choices":[]}""");

    [Theory]
    // The statuses the log actually shows for a model that is loading or swapping. LM Studio answers
    // "model is not loaded" with a 400, which is why 400 cannot be treated as permanent here.
    [InlineData(400, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    // No amount of retrying fixes a wrong key, a wrong URL, or a body the server will never accept.
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(413, false)]
    [InlineData(422, false)]
    // Success is not a retry case at all.
    [InlineData(200, false)]
    public void IsTransient_ClassifiesTheStatus(int status, bool expected) =>
        Assert.Equal(expected, LlmRetryHandler.IsTransient(status));

    [Fact]
    public async Task ARefusalThatClearsIsRetriedUntilItSucceeds()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.BadRequest, """{"error":"model is not loaded"}"""),
            Ok);
        var (http, delays) = Client(inner);

        using var resp = await http.PostAsync("http://llm.test/v1/chat/completions", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Equal([TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task ItGivesUpAfterTheAttemptCap_AndReturnsTheLastResponse()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.InternalServerError, """{"error":"busy"}"""));
        var (http, delays) = Client(inner);

        using var resp = await http.PostAsync("http://llm.test/v1/chat/completions", Json("{}"));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(LlmRetryHandler.MaxAttempts, inner.Calls);
        // The caller still gets the endpoint's own body, so LlmResponse can report what it said.
        Assert.Contains("busy", await resp.Content.ReadAsStringAsync());
        // Backoff grows; it does not hammer.
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)], delays);
    }

    [Fact]
    public async Task APermanentFailureIsReturnedImmediately()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        var (http, delays) = Client(inner);

        using var resp = await http.PostAsync("http://llm.test/v1/chat/completions", Json("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task ASuccessIsNotRetried_AndItsBodyIsUntouched()
    {
        var inner = new ScriptedHandler(ScriptedHandler.Json(HttpStatusCode.OK, """{"choices":[1]}"""));
        var (http, delays) = Client(inner);

        using var resp = await http.PostAsync("http://llm.test/v1/chat/completions", Json("{}"));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
        Assert.Equal("""{"choices":[1]}""", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ARetrySendsTheSameBodyAndCredentials()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"), Ok);
        var (http, _) = Client(inner);

        using var req = new HttpRequestMessage(HttpMethod.Post, "http://llm.test/v1/chat/completions")
        {
            Content = Json("""{"model":"m","messages":[]}"""),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-secret");

        using var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(inner.Bodies[0], inner.Bodies[1]);
        Assert.Equal("""{"model":"m","messages":[]}""", inner.Bodies[1]);
        Assert.Equal(inner.AuthOf(0)!.ToString(), inner.AuthOf(1)!.ToString());
        Assert.Equal("application/json", inner.Requests[1].Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task AConnectionFailureIsRetried()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Throws(new HttpRequestException("connection refused")), Ok);
        var (http, delays) = Client(inner);

        using var resp = await http.PostAsync("http://llm.test/v1/chat/completions", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Single(delays);
    }

    [Fact]
    public async Task AConnectionFailureThatNeverClearsStillThrows()
    {
        var inner = new ScriptedHandler(
            ScriptedHandler.Throws(new HttpRequestException("connection refused")));
        var (http, _) = Client(inner);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => http.PostAsync("http://llm.test/v1/chat/completions", Json("{}")));

        Assert.Equal(LlmRetryHandler.MaxAttempts, inner.Calls);
    }

    [Fact]
    public async Task ANonJsonBodyIsSentExactlyOnce()
    {
        // The dictation upload is a multipart stream of up to 10 MiB of audio. Buffering it to enable a
        // retry would defeat the streaming upload, so that call keeps its single shot.
        var inner = new ScriptedHandler(ScriptedHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        var (http, delays) = Client(inner);

        var form = new MultipartFormDataContent { { new StringContent("audio"), "file", "a.wav" } };
        using var resp = await http.PostAsync("http://llm.test/v1/audio/transcriptions", form);

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task CancellingDuringTheBackoffAbandonsTheCall()
    {
        // The per-call deadline (config.TimeoutSeconds) is a linked token covering the whole chain, so a
        // timeout that expires mid-backoff must end the call rather than being slept through.
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedHandler(ScriptedHandler.Json(HttpStatusCode.BadGateway, "{}"));
        var handler = new LlmRetryHandler(
            NullLogger<LlmRetryHandler>.Instance,
            (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; })
        { InnerHandler = inner };
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            http.PostAsync("http://llm.test/v1/chat/completions", Json("{}"), cts.Token));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task EachAttemptIsItsOwnUsageLogRow()
    {
        // The handler is registered OUTSIDE LlmTelemetryHandler (added first = outermost), so telemetry
        // runs per attempt. Pinned because the alternative ordering is a one-line change that would make
        // the usage log silently understate what a retried call cost - the exact thing that log is for.
        var sink = new FakeLlmUsageSink();
        var inner = new ScriptedHandler(
            ScriptedHandler.Json(HttpStatusCode.BadRequest, """{"error":"model is not loaded"}"""),
            Ok);
        var telemetry = new LlmTelemetryHandler(new FakeLlmTrace(), sink) { InnerHandler = inner };
        var retry = new LlmRetryHandler(
            NullLogger<LlmRetryHandler>.Instance, (_, _) => Task.CompletedTask)
        { InnerHandler = telemetry };
        using var http = new HttpClient(retry);

        using var resp = await http.PostAsync("http://llm.test/v1/chat/completions", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, sink.Calls.Count);
        Assert.Equal("Http400", sink.Calls[0].ErrorKind);
        Assert.Null(sink.Calls[1].ErrorKind);
    }

    [Fact]
    public void TheContainerCanConstructIt()
    {
        // The delay parameter carries a default so there is exactly one constructor and DI has nothing to
        // choose between. Pinned here because a resolution failure would surface nowhere until the first
        // model call in production, which is a BackgroundService nobody is watching.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<LlmRetryHandler>();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<LlmRetryHandler>());
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static (HttpClient Http, List<TimeSpan> Delays) Client(ScriptedHandler inner)
    {
        var delays = new List<TimeSpan>();
        var handler = new LlmRetryHandler(
            NullLogger<LlmRetryHandler>.Instance,
            (d, _) => { delays.Add(d); return Task.CompletedTask; })
        { InnerHandler = inner };
        return (new HttpClient(handler), delays);
    }
}
