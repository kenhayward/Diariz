using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

public class MeetingMinutesClientTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
        [new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Hello there.")];

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public async Task GenerateAsync_PostsToChatCompletions_WithBearerAndModel_AndReturnsMarkdown()
    {
        var handler = new FakeHttpMessageHandler(ChatResponse("# Weekly Sync\n\n## Overview\n\nWe met."));
        var client = new MeetingMinutesClient(new HttpClient(handler));
        var config = new SummarizationRequestConfig("http://llm.test/v1", "sk-secret", "local-model", 60);

        var md = await client.GenerateAsync(config, Segments, meetingDate: null, charBudget: 16000);

        Assert.Equal("# Weekly Sync\n\n## Overview\n\nWe met.", md);
        Assert.Equal("http://llm.test/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", handler.LastRequest.Headers.Authorization!.ToString());
        Assert.Contains("local-model", handler.LastRequestBody);
        Assert.Contains("Hello there.", handler.LastRequestBody);
    }

    [Fact]
    public async Task GenerateAsync_OmitsReasoningEffort_WhenNotConfigured()
    {
        var handler = new FakeHttpMessageHandler(ChatResponse("# x"));
        var client = new MeetingMinutesClient(new HttpClient(handler));
        var config = new SummarizationRequestConfig("http://llm.test/v1", "k", "m", 60);

        await client.GenerateAsync(config, Segments, meetingDate: null, charBudget: 16000);

        Assert.DoesNotContain("reasoning_effort", handler.LastRequestBody);
    }

    [Fact]
    public async Task GenerateAsync_SendsReasoningEffort_WhenConfigured()
    {
        var handler = new FakeHttpMessageHandler(ChatResponse("# x"));
        var client = new MeetingMinutesClient(new HttpClient(handler));
        var config = new SummarizationRequestConfig("http://llm.test/v1", "k", "m", 60) { ReasoningEffort = "high" };

        await client.GenerateAsync(config, Segments, meetingDate: null, charBudget: 16000);

        Assert.Contains("reasoning_effort", handler.LastRequestBody);
        Assert.Contains("high", handler.LastRequestBody);
    }
}
