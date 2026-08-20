using System.Net;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests.Llm;

/// <summary>The one outbound fetch that takes an administrator-supplied URL. Its bounds are the point, so
/// they are what is asserted.</summary>
public class LlmModelDiscoveryClientTests
{
    private const string LmStudioBody =
        """{"data":[{"id":"qwen3.8-27b@q4_k_xl","type":"llm","max_context_length":200000}]}""";
    private const string OpenAiBody = """{"object":"list","data":[{"id":"gpt-4o","object":"model"}]}""";

    private static (LlmModelDiscoveryClient client, RoutingHandler handler) Build(
        Dictionary<string, (HttpStatusCode, string)> routes)
    {
        var handler = new RoutingHandler(routes);
        return (new LlmModelDiscoveryClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Prefers_lm_studios_listing_and_does_not_also_call_the_openai_one()
    {
        // Only LM Studio reports a type and a real context length. Once it has answered there is nothing the
        // OpenAI-compatible listing could add, and a second call would only be a second chance to fail.
        var (client, handler) = Build(new()
        {
            ["/api/v0/models"] = (HttpStatusCode.OK, LmStudioBody),
            ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody),
        });

        var only = Assert.Single(await client.ListAsync("http://lm.test/v1", null));

        Assert.Equal("qwen3.8-27b@q4_k_xl", only.Id);
        Assert.Equal(200_000, only.ContextLength);
        Assert.Equal("llm", only.Kind);
        Assert.DoesNotContain("/v1/models", handler.Paths);
    }

    [Fact]
    public async Task Strips_the_version_segment_when_reaching_for_lm_studios_own_listing()
    {
        // /api/v0/models sits at the server root, beside /v1 rather than under it. Appending it to the
        // configured base would ask for /v1/api/v0/models and always miss.
        var (client, handler) = Build(new() { ["/api/v0/models"] = (HttpStatusCode.OK, LmStudioBody) });

        await client.ListAsync("http://lm.test/v1", null);

        Assert.Equal("/api/v0/models", handler.Paths[0]);
    }

    [Fact]
    public async Task Falls_back_to_the_openai_listing()
    {
        var (client, _) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody) });

        var only = Assert.Single(await client.ListAsync("http://plain.test/v1", null));

        Assert.Equal("gpt-4o", only.Id);
        Assert.Null(only.ContextLength);
        Assert.Null(only.Kind);
    }

    [Fact]
    public async Task Tolerates_a_trailing_slash_on_the_endpoint()
    {
        var (client, handler) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody) });

        Assert.Single(await client.ListAsync("http://plain.test/v1/", null));
        Assert.Contains("/v1/models", handler.Paths);
    }

    [Fact]
    public async Task Returns_nothing_when_both_listings_fail()
    {
        // A wrong URL is the main thing an administrator will hit here. Returning empty lets the endpoint
        // say "no models found"; throwing would surface a 500 from OUR api that says nothing about theirs.
        var (client, _) = Build([]);
        Assert.Empty(await client.ListAsync("http://nothing.test/v1", null));
    }

    [Fact]
    public async Task Returns_nothing_for_an_unusable_url_rather_than_throwing()
    {
        var (client, _) = Build([]);
        Assert.Empty(await client.ListAsync("not a url", null));
    }

    [Fact]
    public async Task Sends_the_key_as_a_bearer_token_when_one_is_given()
    {
        var (client, handler) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody) });

        await client.ListAsync("http://plain.test/v1", "sk-secret");

        Assert.All(handler.AuthHeaders, h => Assert.Equal("Bearer sk-secret", h));
    }

    [Fact]
    public async Task Sends_no_authorization_header_when_there_is_no_key()
    {
        // Normal for a local endpoint. An empty Bearer is not the same as no header, and some servers
        // reject it.
        var (client, handler) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, OpenAiBody) });

        await client.ListAsync("http://plain.test/v1", null);

        Assert.All(handler.AuthHeaders, Assert.Null);
    }

    [Fact]
    public async Task Treats_a_redirect_as_a_failure_rather_than_a_listing()
    {
        // A cooperating host must not be able to bounce this request onward to somewhere the administrator
        // never named - that is the whole reason a caller-supplied URL was contentious.
        //
        // What this test can prove is the half that lives HERE: a 3xx is not a success, so it yields nothing
        // and this class never chases the Location header itself. The other half - the transport not
        // following it automatically - is `AllowAutoRedirect = false` on the primary handler in Program.cs,
        // and it is out of reach from a unit test, because substituting a fake handler is precisely what
        // removes the redirect-following being asserted. Do not read a pass here as covering that.
        var (client, handler) = Build(new()
        {
            ["/api/v0/models"] = (HttpStatusCode.Redirect, ""),
            ["/v1/models"] = (HttpStatusCode.Redirect, ""),
        });

        Assert.Empty(await client.ListAsync("http://redirector.test/v1", null));
        Assert.Equal(2, handler.Paths.Count);   // one attempt each, no follow-on of our own
    }

    [Fact]
    public async Task Reads_only_a_bounded_amount_from_the_endpoint()
    {
        // A hostile or broken endpoint must not be able to stream unbounded data into memory. The cap is
        // generous enough for any real listing, so a truncated body simply parses to nothing.
        var giant = """{"data":[""" + string.Join(",",
            Enumerable.Range(0, 40_000).Select(i => $$"""{"id":"model-{{i}}"}""")) + "]}";
        var (client, _) = Build(new() { ["/v1/models"] = (HttpStatusCode.OK, giant) });

        var models = await client.ListAsync("http://flood.test/v1", null);

        Assert.True(
            models.Count < 40_000,
            $"expected the read to be capped, but {models.Count} models came back");
    }
}
