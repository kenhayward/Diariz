using System.Net;
using System.Text;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Answers each request from a per-path script, and records every path and Authorization header it
/// was asked for.
///
/// A sibling to <see cref="CapturingHandler"/> rather than an option on it: that one answers one canned
/// response and captures the request BODY, which is what the LLM clients need. This one answers differently
/// per path and captures which paths were tried, which is what a client that falls back from one endpoint to
/// another needs - the interesting assertion there is "it did not also call the other one".</summary>
public sealed class RoutingHandler(Dictionary<string, (HttpStatusCode Status, string Body)> routes)
    : HttpMessageHandler
{
    /// <summary>Absolute paths requested, in order.</summary>
    public List<string> Paths { get; } = [];

    /// <summary>The Authorization header of each request, null where none was set.</summary>
    public List<string?> AuthHeaders { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        AuthHeaders.Add(request.Headers.Authorization?.ToString());

        var (status, body) = routes.TryGetValue(request.RequestUri.AbsolutePath, out var r)
            ? r
            : (HttpStatusCode.NotFound, "");

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
