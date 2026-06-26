using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class SummarizationClientTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
        [new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Hello there.")];

    private static string ChatResponse(string content)
    {
        var obj = new { choices = new[] { new { message = new { content } } } };
        return JsonSerializer.Serialize(obj);
    }

    [Fact]
    public async Task SummarizeAsync_PostsToChatCompletions_WithBearerAndModel_AndParsesResult()
    {
        var handler = new FakeHttpMessageHandler(
            ChatResponse("{\"summary\":\"Quick chat.\",\"name\":\"Greeting\"}"));
        var http = new HttpClient(handler);
        var client = new SummarizationClient(http);
        var config = new SummarizationRequestConfig("http://llm.test/v1", "sk-secret", "local-model", 60);

        var result = await client.SummarizeAsync(config, Segments, needName: true);

        Assert.Equal("Quick chat.", result.Summary);
        Assert.Equal("Greeting", result.Name);
        Assert.Equal("http://llm.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", handler.LastRequest.Headers.Authorization!.ToString());
        Assert.Contains("local-model", handler.LastRequestBody);
        Assert.Contains("Hello there.", handler.LastRequestBody);
    }
}
