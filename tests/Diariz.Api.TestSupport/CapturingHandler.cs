using System.Net;
using System.Text;
using System.Text.Json;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Captures the JSON body of the last request and returns a canned response.
///
/// The only way to assert what a client actually put on the wire: the clients build their bodies
/// internally, so there is no seam above HttpClient to inspect. Before this existed nothing asserted any
/// request body at all - the hardcoded temperatures could have changed and the suite would have stayed
/// green.</summary>
public sealed class CapturingHandler : DelegatingHandler
{
    private readonly string _responseJson;
    private readonly HttpStatusCode _status;

    public CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseJson = responseJson;
        _status = status;
        InnerHandler = new NoopHandler();
    }

    public string? LastBodyRaw { get; private set; }
    public Uri? LastRequestUri { get; private set; }

    /// <summary>The captured body parsed as JSON. Throws when nothing was captured, which is itself the
    /// useful behaviour: a test asserting on the body when no request was made must not quietly pass.</summary>
    public JsonElement LastBody =>
        JsonDocument.Parse(LastBodyRaw ?? throw new InvalidOperationException("no request was captured"))
            .RootElement;

    /// <summary>True when the captured body has this key at the top level.</summary>
    public bool Has(string key) => LastBody.TryGetProperty(key, out _);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        if (request.Content is not null)
            LastBodyRaw = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) =>
            throw new NotSupportedException("CapturingHandler never forwards to an inner handler");
    }
}
