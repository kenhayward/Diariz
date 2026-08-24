using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Inner handler that plays a scripted sequence of outcomes, one per request, and records what it
/// was sent each time.
///
/// <para>The existing fakes answer every request the same way, which cannot express the thing a retry is
/// about: a call that fails and then succeeds. Each entry is either a status plus body, or an exception to
/// throw in place of a response (a connection failure). The last entry repeats once the script runs
/// out, so a test only scripts the part it cares about.</para></summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _script;
    private Func<HttpResponseMessage>? _last;

    public ScriptedHandler(params Func<HttpResponseMessage>[] script) =>
        _script = new Queue<Func<HttpResponseMessage>>(script);

    /// <summary>Every request body seen, in order - so a test can assert a retry re-sent the same bytes.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>Every request seen, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    public int Calls => Requests.Count;

    /// <summary>A scripted response with a JSON body.</summary>
    public static Func<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>A scripted transport failure - the endpoint refusing the connection outright.</summary>
    public static Func<HttpResponseMessage> Throws(Exception ex) => () => throw ex;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

        if (_script.Count > 0) _last = _script.Dequeue();
        return (_last ?? Json(HttpStatusCode.OK, "{}"))();
    }

    /// <summary>The <c>Authorization</c> header on the n-th (0-based) request, so a test can assert a retry
    /// carried the credentials the first attempt did.</summary>
    public AuthenticationHeaderValue? AuthOf(int index) => Requests[index].Headers.Authorization;
}
