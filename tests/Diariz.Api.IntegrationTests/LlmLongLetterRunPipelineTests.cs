using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// Every LLM client must break long letter runs before a request leaves (issue #841), or one Japanese meeting
/// or one Whisper loop fails that client's step for the recording, permanently. The handler is unit-tested on
/// its own; this pins that the real <c>Program.cs</c> host actually puts it in each client's pipeline - and
/// OUTSIDE the retry handler, so a retried attempt resends the rewritten body rather than the original.
/// Like <see cref="LlmHttpClientTimeoutTests"/> it lists the registrations by hand: a client added later must
/// be added here too.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LlmLongLetterRunPipelineTests(ContainersFixture fx)
{
    [Theory]
    [InlineData(nameof(IChatStreamClient))]
    [InlineData(nameof(ISummarizationClient))]
    [InlineData(nameof(IActionsClient))]
    [InlineData(nameof(ITranslationClient))]
    [InlineData(nameof(IMeetingMinutesClient))]
    [InlineData(nameof(ITagsClient))]
    [InlineData(nameof(IOcrClient))]
    public void LlmClients_BreakLongLetterRuns_BeforeAnyRetry(string clientName)
    {
        using var factory = new DiarizWebAppFactory(fx);
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        var chain = new List<Type>();
        for (var h = handler as DelegatingHandler; h is not null; h = h.InnerHandler as DelegatingHandler)
            chain.Add(h.GetType());

        var breaker = chain.IndexOf(typeof(LongLetterRunHandler));
        Assert.True(breaker >= 0, $"{clientName} pipeline: {string.Join(" -> ", chain.Select(t => t.Name))}");
        Assert.True(breaker < chain.IndexOf(typeof(LlmRetryHandler)),
            $"{clientName} pipeline: {string.Join(" -> ", chain.Select(t => t.Name))}");
    }
}
