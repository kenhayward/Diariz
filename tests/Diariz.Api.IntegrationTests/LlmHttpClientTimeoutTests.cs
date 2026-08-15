using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// Every LLM client must disable HttpClient's own 100s cap so the configured per-request timeout
/// (user -&gt; platform -&gt; server option) is the single authority. <see cref="IChatStreamClient"/> was once
/// registered without it, which silently capped chat streaming AND formula runs at 100s no matter what
/// was configured. This asserts the rule across several clients (not just the one that was broken), so a
/// future client registered without <c>NoHttpTimeout</c> is likelier to be caught.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LlmHttpClientTimeoutTests(ContainersFixture fx)
{
    private DiarizWebAppFactory NewFactory() => new(fx);

    [Theory]
    [InlineData(nameof(IChatStreamClient))]
    [InlineData(nameof(ISummarizationClient))]
    [InlineData(nameof(IEmbeddingClient))]
    public void LlmClients_HaveNoHttpClientTimeout(string clientName)
    {
        using var factory = NewFactory();
        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, http.Timeout);
    }
}
