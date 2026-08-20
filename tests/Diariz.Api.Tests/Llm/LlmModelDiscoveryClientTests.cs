using System.Net;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests.Llm;

/// <summary>The one outbound fetch that takes an administrator-supplied URL.
///
/// Two things are proved here, and the second was a shipped defect: the bounds on the fetch, and that what
/// comes back names an endpoint <b>chat can actually call</b>. Discovery used to probe LM Studio's
/// <c>/api/v0/models</c> at the SERVER ROOT, which answers whatever path the administrator typed - so a base
/// URL missing <c>/v1</c> validated perfectly, and every completion then went to <c>/chat/completions</c> at
/// the root, which LM Studio answers 200 and never streams.</summary>
public class LlmModelDiscoveryClientTests
{
    private const string LmStudioBody =
        """{"data":[{"id":"qwen3.8-27b@q4_k_xl","type":"llm","max_context_length":200000}]}""";
    private const string OpenAiBody = """{"object":"list","data":[{"id":"gpt-4o","object":"model"}]}""";

    /// <summary>What LM Studio really returns for a path it does not serve, verified against a live server.
    /// The status is <b>200</b>, which is exactly why a status check cannot tell a working endpoint from a
    /// wrong one - only parsing the body can.</summary>
    private const string UnknownEndpointBody =
        """{"error":"Unexpected endpoint or method. (GET /models)"}""";

    private static (LlmModelDiscoveryClient client, RoutingHandler handler) Build(
        Dictionary<string, (HttpStatusCode, string)> routes)
    {
        var handler = new RoutingHandler(routes);
        return (new LlmModelDiscoveryClient(new HttpClient(handler)), handler);
    }

    /// <summary>An LM Studio server as they really behave: rich metadata at the root, an OpenAI listing under
    /// /v1, and a cheerful 200 plus an error body for anything else.</summary>
    private static Dictionary<string, (HttpStatusCode, string)> LmStudio() => new()
    {
        ["/api/v0/models"] = (HttpStatusCode.OK, LmStudioBody),
        ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody),
        ["/models"] = (HttpStatusCode.OK, UnknownEndpointBody),
    };

    // ---- Which endpoint the models will actually be called on ----

    [Fact]
    public async Task Corrects_a_base_url_that_is_missing_the_version_segment()
    {
        // The shipped defect, as reported: the address is typed without /v1, discovery succeeds anyway, and
        // every chat call then posts to /chat/completions at the root and never gets a reply.
        var (client, _) = Build(LmStudio());

        var listing = await client.ListAsync("http://lm.test:1234", null);

        Assert.Equal("http://lm.test:1234/v1", listing.ChatApiBase);
    }

    [Fact]
    public async Task Keeps_a_base_url_that_already_works()
    {
        var (client, _) = Build(LmStudio());

        var listing = await client.ListAsync("http://lm.test:1234/v1", null);

        Assert.Equal("http://lm.test:1234/v1", listing.ChatApiBase);
    }

    [Fact]
    public async Task Does_not_accept_a_200_that_is_not_a_model_listing()
    {
        // LM Studio answers 200 for a path it does not serve. Treating the status as the answer is what let
        // the wrong base through, so nothing here may rely on it.
        var (client, _) = Build(new()
        {
            ["/models"] = (HttpStatusCode.OK, UnknownEndpointBody),
            ["/v1/models"] = (HttpStatusCode.OK, UnknownEndpointBody),
        });

        var listing = await client.ListAsync("http://lm.test:1234", null);

        Assert.Null(listing.ChatApiBase);
    }

    [Fact]
    public async Task Names_no_endpoint_when_the_server_serves_no_model_listing()
    {
        // Metadata alone is not enough to import: without an endpoint chat can call, every row created would
        // be one that cannot answer.
        var (client, _) = Build(new() { ["/api/v0/models"] = (HttpStatusCode.OK, LmStudioBody) });

        var listing = await client.ListAsync("http://lm.test:1234", null);

        Assert.Null(listing.ChatApiBase);
        Assert.NotEmpty(listing.Models);   // so the caller can say "found models, but no usable endpoint"
    }

    [Fact]
    public async Task Tolerates_a_trailing_slash_on_the_endpoint()
    {
        var (client, _) = Build(LmStudio());

        var listing = await client.ListAsync("http://lm.test:1234/v1/", null);

        Assert.Equal("http://lm.test:1234/v1", listing.ChatApiBase);
    }

    // ---- What the models are ----

    [Fact]
    public async Task Prefers_lm_studios_metadata_for_type_and_context_length()
    {
        // The OpenAI listing reports neither, so the richer source wins even though both are consulted -
        // one for the models, one to confirm the endpoint.
        var (client, _) = Build(LmStudio());

        var only = Assert.Single((await client.ListAsync("http://lm.test:1234/v1", null)).Models);

        Assert.Equal("qwen3.8-27b@q4_k_xl", only.Id);
        Assert.Equal(200_000, only.ContextLength);
        Assert.Equal("llm", only.Kind);
    }

    [Fact]
    public async Task Falls_back_to_the_openai_listing_for_a_plain_server()
    {
        var (client, _) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody) });

        var listing = await client.ListAsync("http://plain.test/v1", null);

        var only = Assert.Single(listing.Models);
        Assert.Equal("gpt-4o", only.Id);
        Assert.Null(only.ContextLength);
        Assert.Null(only.Kind);
        Assert.Equal("http://plain.test/v1", listing.ChatApiBase);
    }

    // ---- Bounds on the fetch ----

    [Fact]
    public async Task Returns_nothing_when_the_server_cannot_be_reached()
    {
        // A wrong URL is the main thing an administrator will hit here. Returning empty lets the endpoint
        // say "no models found"; throwing would surface a 500 from OUR api that says nothing about theirs.
        var (client, _) = Build([]);

        var listing = await client.ListAsync("http://nothing.test/v1", null);

        Assert.Empty(listing.Models);
        Assert.Null(listing.ChatApiBase);
    }

    [Fact]
    public async Task Returns_nothing_for_an_unusable_url_rather_than_throwing()
    {
        var (client, _) = Build([]);
        Assert.Empty((await client.ListAsync("not a url", null)).Models);
    }

    [Fact]
    public async Task Sends_the_key_as_a_bearer_token_when_one_is_given()
    {
        var (client, handler) = Build(LmStudio());

        await client.ListAsync("http://lm.test:1234/v1", "sk-secret");

        Assert.NotEmpty(handler.AuthHeaders);
        Assert.All(handler.AuthHeaders, h => Assert.Equal("Bearer sk-secret", h));
    }

    [Fact]
    public async Task Sends_no_authorization_header_when_there_is_no_key()
    {
        // Normal for a local endpoint. An empty Bearer is not the same as no header, and some servers
        // reject it.
        var (client, handler) = Build(LmStudio());

        await client.ListAsync("http://lm.test:1234/v1", null);

        Assert.All(handler.AuthHeaders, Assert.Null);
    }

    [Fact]
    public async Task Treats_a_redirect_as_a_failure_rather_than_a_listing()
    {
        // A cooperating host must not be able to bounce this request onward to somewhere the administrator
        // never named - that is the whole reason a caller-supplied URL was contentious.
        //
        // What this test can prove is the half that lives HERE: a 3xx is not a success, so it yields nothing
        // and this class never chases the Location header. The other half - the transport not following it
        // automatically - is `AllowAutoRedirect = false` on the primary handler in Program.cs, and it is out
        // of reach from a unit test, because substituting a fake handler is precisely what removes the
        // redirect-following being asserted. Do not read a pass here as covering that.
        var (client, _) = Build(new()
        {
            ["/api/v0/models"] = (HttpStatusCode.Redirect, ""),
            ["/v1/models"] = (HttpStatusCode.Redirect, ""),
            ["/models"] = (HttpStatusCode.Redirect, ""),
        });

        var listing = await client.ListAsync("http://redirector.test/v1", null);

        Assert.Empty(listing.Models);
        Assert.Null(listing.ChatApiBase);
    }

    [Fact]
    public async Task Reads_only_a_bounded_amount_from_the_endpoint()
    {
        // A hostile or broken endpoint must not be able to stream unbounded data into memory. The cap is
        // generous enough for any real listing, so a truncated body simply parses to nothing.
        var giant = """{"data":[""" + string.Join(",",
            Enumerable.Range(0, 40_000).Select(i => $$"""{"id":"model-{{i}}"}""")) + "]}";
        var (client, _) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, giant) });

        var listing = await client.ListAsync("http://flood.test/v1", null);

        Assert.True(
            listing.Models.Count < 40_000,
            $"expected the read to be capped, but {listing.Models.Count} models came back");
    }
}
