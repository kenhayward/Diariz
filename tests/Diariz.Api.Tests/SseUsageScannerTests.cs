using System.Text;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SseUsageScannerTests
{
    private static void FeedAll(SseUsageScanner scanner, string text) =>
        scanner.Feed(Encoding.UTF8.GetBytes(text));

    private const string UsageChunk =
        """data: {"id":"x","choices":[],"usage":{"prompt_tokens":69,"completion_tokens":5,"total_tokens":74,"completion_tokens_details":{"reasoning_tokens":2}}}""";

    [Fact]
    public void FindsTheUsageChunk_InAFullStream()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" + UsageChunk + "\n\ndata: [DONE]\n\n");

        Assert.Equal(69, scanner.Usage!.Value.PromptTokens);
        Assert.Equal(5, scanner.Usage!.Value.CompletionTokens);
        Assert.Equal(2, scanner.Usage!.Value.ReasoningTokens);
    }

    [Fact]
    public void FindsIt_WhenTheChunkIsSplitAcrossFeeds()
    {
        // The whole reason this is byte-oriented: a chunk WILL be split across socket reads, and a scanner
        // that only handled whole lines would silently miss usage on exactly the large responses that
        // matter most.
        var scanner = new SseUsageScanner();
        var bytes = Encoding.UTF8.GetBytes(UsageChunk + "\n\n");
        scanner.Feed(bytes.AsSpan(0, 30));
        scanner.Feed(bytes.AsSpan(30));

        Assert.Equal(74, scanner.Usage!.Value.TotalTokens);
    }

    [Fact]
    public void FindsIt_WhenSplitMidMultiByteCharacter()
    {
        // Content deltas carry arbitrary UTF-8. A split inside a multi-byte sequence must not corrupt the
        // buffer or throw - it must simply resolve once the rest arrives.
        var scanner = new SseUsageScanner();
        var bytes = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"éèê\"}}]}\n\n" + UsageChunk + "\n\n");
        for (var i = 0; i < bytes.Length; i++) scanner.Feed(bytes.AsSpan(i, 1)); // one byte at a time

        Assert.Equal(74, scanner.Usage!.Value.TotalTokens);
    }

    [Fact]
    public void KeepsTheLastUsage_WhenMoreThanOneAppears()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, """data: {"usage":{"total_tokens":10}}""" + "\n\n" + """data: {"usage":{"total_tokens":99}}""" + "\n\n");

        Assert.Equal(99, scanner.Usage!.Value.TotalTokens);
    }

    [Fact]
    public void UsageIsNull_WhenTheStreamNeverReportsAny()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n");

        Assert.Null(scanner.Usage);
    }

    [Fact]
    public void AbsurdlyLongLine_DoesNotGrowTheBufferWithoutBound()
    {
        // A hostile or broken endpoint must not be able to make a telemetry scanner exhaust memory. The
        // scanner drops an over-long line and resynchronises at the next newline.
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: " + new string('x', SseUsageScanner.MaxLineBytes * 2) + "\n\n" + UsageChunk + "\n\n");

        Assert.Equal(74, scanner.Usage!.Value.TotalTokens); // resynchronised and still found it
    }

    [Fact]
    public void NeverThrows_OnGarbage()
    {
        var scanner = new SseUsageScanner();
        var ex = Record.Exception(() => FeedAll(scanner, "data: not json\n\n\0\0\0\ndata: {\"usage\":\n"));
        Assert.Null(ex);
    }

    [Fact]
    public void RetainsNoContent()
    {
        // The scanner sees every content delta go past. It must keep none of them - this table is browsed
        // by an administrator and meeting content must never reach it.
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: {\"choices\":[{\"delta\":{\"content\":\"the secret merger closes friday\"}}]}\n\n" + UsageChunk + "\n\n");

        var serialized = System.Text.Json.JsonSerializer.Serialize(scanner.Usage);
        Assert.DoesNotContain("secret merger", serialized);
    }
}
