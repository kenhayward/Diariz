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
    public void AbsurdlyLongLine_KeepsTheBufferBounded()
    {
        // A hostile or broken endpoint must not be able to make a telemetry scanner exhaust memory. Feed a
        // run of bytes with NO newline, well past MaxLineBytes, and check the buffer never exceeds the
        // bound - not merely that the scanner recovers afterward (an unbounded buffer would recover too,
        // since the junk line contains no "usage" substring either way).
        var scanner = new SseUsageScanner();
        var junk = Encoding.UTF8.GetBytes("data: " + new string('x', SseUsageScanner.MaxLineBytes * 3));

        // Feed it in chunks, checking the bound holds throughout, not just at the end.
        const int chunkSize = 4096;
        for (var offset = 0; offset < junk.Length; offset += chunkSize)
        {
            var len = Math.Min(chunkSize, junk.Length - offset);
            scanner.Feed(junk.AsSpan(offset, len));
            Assert.True(scanner.BufferedBytes <= SseUsageScanner.MaxLineBytes);
        }

        Assert.True(scanner.BufferedBytes <= SseUsageScanner.MaxLineBytes);
    }

    [Fact]
    public void AbsurdlyLongLine_IsDroppedAndTheScannerResynchronises()
    {
        // Separate from the bound check above: after the over-long line ends, the scanner recovers and
        // still finds a usage chunk that follows it.
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
        // The scanner sees every content delta go past. It must keep none of them once a line is complete -
        // this table is browsed by an administrator and meeting content must never reach it. (That `Usage`
        // itself can only ever carry counts, never text, is guaranteed by LlmUsage's type - four nullable
        // ints, no string field - not by this test; this test instead proves the scanner does not squirrel
        // the raw line away anywhere else after processing it.)
        var scanner = new SseUsageScanner();
        FeedAll(scanner, "data: {\"choices\":[{\"delta\":{\"content\":\"the secret merger closes friday\"}}]}\n\n");

        Assert.Equal(0, scanner.BufferedBytes);
    }

    // ---- finish_reason (0.222.0) ----

    /// A reply cut off by max_tokens looks, to a user, exactly like a model that answered nothing: the
    /// content is empty and no error is raised. finish_reason is the only thing that tells them apart, so
    /// the scanner has to catch it in the stream the same way it catches usage.
    [Fact]
    public void CapturesALengthFinishReason_FromAStreamedChunk()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, DELTA + BLANK);
        Assert.Null(scanner.FinishReason);

        FeedAll(scanner, LENGTH + BLANK);
        Assert.Equal("length", scanner.FinishReason);
    }

    [Fact]
    public void CapturesAStopFinishReason()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, STOP + BLANK);
        Assert.Equal("stop", scanner.FinishReason);
    }

    /// Servers differ on whitespace after the colon, and a scanner matching only the compact form would
    /// silently report nothing for half of them.
    [Fact]
    public void CapturesAFinishReason_WrittenWithSpacesAfterTheColon()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, SPACED + BLANK);
        Assert.Equal("length", scanner.FinishReason);
    }

    /// The pre-filter exists so a chat's every token does not pay for a JSON parse. A null finish_reason
    /// rides on essentially every delta chunk, so matching the bare key name would defeat it entirely.
    [Fact]
    public void DoesNotParseEveryDelta_JustBecauseItCarriesANullFinishReason()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, DELTA + BLANK);
        FeedAll(scanner, SPACED_NULL + BLANK);

        Assert.Null(scanner.FinishReason);
    }

    /// The last one wins: a stream stops once, and a later chunk is closer to the truth than an earlier one.
    [Fact]
    public void KeepsTheLastFinishReasonSeen()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, STOP + BLANK);
        FeedAll(scanner, LENGTH + BLANK);

        Assert.Equal("length", scanner.FinishReason);
    }

    [Fact]
    public void FindsBothUsageAndFinishReason_InOneStream()
    {
        var scanner = new SseUsageScanner();
        FeedAll(scanner, LENGTH + BLANK + UsageChunk + BLANK);

        Assert.Equal("length", scanner.FinishReason);
        Assert.Equal(5, scanner.Usage!.Value.CompletionTokens);
    }

    private const string BLANK = "\n\n";
    private const string DELTA =
        """data: {"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""";
    private const string SPACED_NULL =
        """data: {"choices":[{"delta":{"content":"b"},"finish_reason": null}]}""";
    private const string LENGTH =
        """data: {"choices":[{"delta":{},"finish_reason":"length"}]}""";
    private const string STOP =
        """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""";
    private const string SPACED =
        """data: {"choices":[{"delta":{}, "finish_reason" : "length"}]}""";
}
