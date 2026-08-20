using System.Net.Http.Headers;

namespace Diariz.Api.Services.Llm;

/// <summary>What a server reported, and the endpoint its models must actually be called on.
///
/// <paramref name="ChatApiBase"/> is null when nothing at that address serves an OpenAI-compatible model
/// listing. That is deliberately distinct from finding no models: a server can report models through
/// LM Studio's own endpoint while the base URL given is not one <c>/chat/completions</c> is served from, and
/// importing rows against it would create models that cannot answer.</summary>
public sealed record ModelListing(string? ChatApiBase, IReadOnlyList<DiscoveredModel> Models)
{
    public static readonly ModelListing None = new(null, []);
}

public interface ILlmModelDiscoveryClient
{
    /// <summary>Asks an OpenAI-compatible server what models it has, and resolves the endpoint they must be
    /// called on. Failures return an empty listing rather than throwing - a wrong URL is the main thing being
    /// diagnosed here.</summary>
    Task<ModelListing> ListAsync(string apiBase, string? apiKey, CancellationToken ct = default);
}

/// <summary>Asks an OpenAI-compatible server what models it has.
///
/// <b>This is the only endpoint in the platform that fetches an administrator-supplied URL.</b> Its
/// neighbour, <c>POST /api/admin/llm-models/{id}/test</c>, deliberately refuses one: accepting a
/// caller-supplied URL turns an administrator's session into a way of reaching arbitrary hosts with no model
/// row left behind as an audit trail. That relaxation was made knowingly here, because onboarding a server
/// with forty models by hand is the problem being solved, and it is bounded rather than open:
///
/// <list type="bullet">
/// <item>the route is <c>ManagePlatform</c> only;</item>
/// <item>the request times out after <see cref="TimeoutSeconds"/> seconds;</item>
/// <item>redirects are NOT followed, so a cooperating host cannot bounce the request onward to somewhere
/// the administrator never named;</item>
/// <item>the response is read to a cap, so a hostile endpoint cannot stream unbounded data into memory;</item>
/// <item><b>only parsed model ids leave this class.</b> The raw body is never returned to the caller, so
/// this cannot be used as a general-purpose fetch.</item>
/// </list>
///
/// <b>There is deliberately no IP guard</b>, unlike <c>UrlFetchGuard</c> on URL attachments and webhooks.
/// Those fetch a URL a user supplied about the outside world, so loopback and private ranges are always
/// wrong. Here the opposite is true: the platform's own models routinely live on localhost or the LAN - the
/// reference deployment is an LM Studio box at a 192.168.x.x address - so blocking private ranges would
/// block the primary use case. The compensating control is that only a Platform Administrator can call it,
/// and that nothing of the response but model ids is ever echoed back.
///
/// <b>Two endpoints are consulted, for two different jobs.</b> LM Studio's <c>/api/v0/models</c> supplies the
/// metadata - a type and a real context length, neither of which the OpenAI-compatible listing reports - but
/// it answers at the SERVER ROOT regardless of the path given, so it confirms the server exists and nothing
/// whatever about the base URL. The OpenAI-compatible <c>{base}/models</c> is therefore also fetched, to
/// resolve the endpoint completions will actually be posted to. Skipping that is what shipped a base URL
/// missing its <c>/v1</c>: discovery passed, and every chat call then went to <c>/chat/completions</c> at the
/// root, which LM Studio answers <c>200</c> and never streams, so replies never arrived.</summary>
public sealed class LlmModelDiscoveryClient(HttpClient http) : ILlmModelDiscoveryClient
{
    private const int TimeoutSeconds = 10;

    /// <summary>Generous for any real listing - LM Studio reports a few hundred bytes per model - and small
    /// enough that a misbehaving endpoint cannot exhaust memory. A body that exceeds it is truncated, which
    /// then parses to nothing rather than being read to the end.</summary>
    private const int MaxResponseBytes = 512 * 1024;

    public async Task<ModelListing> ListAsync(string apiBase, string? apiKey, CancellationToken ct = default)
    {
        var trimmed = apiBase.TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri)) return ModelListing.None;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        // LM Studio's own listing sits at the SERVER ROOT, beside /v1 rather than under it, so the version
        // segment has to come off. Appending it to the configured base would ask for /v1/api/v0/models and
        // always miss, silently costing every import its real context lengths.
        var root = new Uri(baseUri, "/").ToString().TrimEnd('/');
        var lmStudioBody = await GetAsync(new Uri($"{root}/api/v0/models"), apiKey, deadline.Token);
        var richModels = lmStudioBody is null ? [] : LlmModelDiscovery.ParseLmStudio(lmStudioBody);

        // Resolve the endpoint the models will actually be CALLED on, by finding one that serves an
        // OpenAI-compatible listing. This is the whole point: LM Studio's metadata endpoint answers at the
        // root whatever path was typed, so it confirms the server exists and nothing about the base URL.
        // A base missing /v1 therefore used to sail through discovery and then send every completion to
        // /chat/completions at the root, which LM Studio answers 200 and never streams.
        //
        // The typed base is tried first so a deliberate non-standard path is respected; /v1 is the fallback
        // because it is where OpenAI-compatible servers put it.
        foreach (var candidate in new[] { trimmed, $"{root}/v1" }.Distinct())
        {
            var body = await GetAsync(new Uri($"{candidate}/models"), apiKey, deadline.Token);
            if (body is null) continue;

            var openAi = LlmModelDiscovery.ParseOpenAi(body);
            // Parsed, not merely 200. LM Studio returns 200 with an {"error": ...} body for a path it does
            // not serve, so the status says nothing - only a listing that parses proves the endpoint works.
            if (openAi.Count == 0) continue;

            return new ModelListing(candidate, richModels.Count > 0 ? richModels : openAi);
        }

        // Reached the server but found nowhere to call it. Models may still be reported, so the caller can
        // tell "no models" apart from "models, but no usable endpoint" and say something useful.
        return new ModelListing(null, richModels);
    }

    /// <summary>The body of a successful GET, or null for anything else. A non-success status is a fact
    /// about their server, not an error in ours, so it is reported by returning nothing.</summary>
    private async Task<string?> GetAsync(Uri uri, string? apiKey, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            // Absent, not empty: some servers reject a blank Bearer, and no key at all is normal for a
            // local endpoint.
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            return await ReadCappedAsync(resp, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException
                                      or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Reads at most <see cref="MaxResponseBytes"/>. Read explicitly rather than via
    /// <c>ReadAsStringAsync</c> so the cap applies to what is actually pulled off the socket, not to a
    /// string that has already been fully buffered.</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (buffer.Length < MaxResponseBytes)
        {
            var read = await stream.ReadAsync(
                chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaxResponseBytes - buffer.Length)), ct);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
