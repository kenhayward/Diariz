using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// Every LLM client must disable HttpClient's own 100s cap so the configured per-request timeout
/// (user -&gt; platform -&gt; server option) is the single authority. <see cref="IChatStreamClient"/> was once
/// registered without it, which silently capped chat streaming AND formula runs at 100s no matter what
/// was configured. This pins every <c>AddLlmClient</c> registration in <c>Program.cs</c> as of this
/// writing (all eight): it does not automatically discover new registrations, so a client added later
/// must also be added to this theory's <c>InlineData</c> list, or it will not be covered.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LlmHttpClientTimeoutTests(ContainersFixture fx)
{
    private DiarizWebAppFactory NewFactory() => new(fx);

    [Theory]
    [InlineData(nameof(IChatStreamClient))]
    [InlineData(nameof(ISummarizationClient))]
    [InlineData(nameof(IEmbeddingClient))]
    [InlineData(nameof(IDictationClient))]
    [InlineData(nameof(IActionsClient))]
    [InlineData(nameof(ITranslationClient))]
    [InlineData(nameof(IMeetingMinutesClient))]
    [InlineData(nameof(ITagsClient))]
    public void LlmClients_HaveNoHttpClientTimeout(string clientName)
    {
        using var factory = NewFactory();
        var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, http.Timeout);
    }
}
