using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptChunkerTests
{
    // Each line renders as "Speaker: Text\n"; craft texts of a known length so char-budget windowing
    // is deterministic in the tests below.
    private static ChunkSegment Seg(string speaker, long start, long end, string text) =>
        new(speaker, start, end, text);

    private static ChunkSegment Sized(string speaker, int index, int textLen) =>
        new(speaker, index * 1000, index * 1000 + 900, new string((char)('a' + index % 26), textLen));

    [Fact]
    public void Chunk_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TranscriptChunker.Chunk([]));
    }

    [Fact]
    public void Chunk_SingleShortSegment_IsOneChunk()
    {
        var chunks = TranscriptChunker.Chunk([Seg("Alice", 0, 1000, "Hello there.")]);

        var c = Assert.Single(chunks);
        Assert.Equal(0, c.Ordinal);
        Assert.Equal(0, c.StartMs);
        Assert.Equal(1000, c.EndMs);
        Assert.Equal(["Alice"], c.Speakers);
        Assert.Contains("Alice: Hello there.", c.Text);
    }

    [Fact]
    public void Chunk_SegmentsUnderBudget_AllInOneChunk_WithTimesAndSpeakerSet()
    {
        var chunks = TranscriptChunker.Chunk(
        [
            Seg("Alice", 0, 1000, "We ship Friday."),
            Seg("Bob", 1000, 2000, "I'll write the tests."),
            Seg("Alice", 2000, 3500, "Great."),
        ], targetChars: 10_000);

        var c = Assert.Single(chunks);
        Assert.Equal(0, c.StartMs);
        Assert.Equal(3500, c.EndMs);              // last segment's end
        Assert.Equal(["Alice", "Bob"], c.Speakers); // distinct, first-seen order
        Assert.Contains("We ship Friday.", c.Text);
        Assert.Contains("I'll write the tests.", c.Text);
        Assert.Contains("Great.", c.Text);
    }

    [Fact]
    public void Chunk_ExceedsBudget_SplitsWithSequentialOrdinalsAndOverlap()
    {
        // 4 segments ~68 chars each ("X: " + 60 chars + newline); target 100 => ~2 segments/chunk, overlap 1.
        var segs = new[]
        {
            Sized("A", 0, 60), Sized("B", 1, 60), Sized("C", 2, 60), Sized("D", 3, 60),
        };

        var chunks = TranscriptChunker.Chunk(segs, targetChars: 100, overlapSegments: 1);

        Assert.Equal(3, chunks.Count);
        Assert.Equal([0, 1, 2], chunks.Select(c => c.Ordinal).ToArray());
        // Overlap: each chunk after the first re-includes the previous chunk's trailing segment.
        Assert.Contains(segs[0].Text, chunks[0].Text);
        Assert.Contains(segs[1].Text, chunks[0].Text);
        Assert.Contains(segs[1].Text, chunks[1].Text); // shared boundary segment
        Assert.Contains(segs[2].Text, chunks[1].Text);
        Assert.Contains(segs[2].Text, chunks[2].Text); // shared boundary segment
        Assert.Contains(segs[3].Text, chunks[2].Text);
    }

    [Fact]
    public void Chunk_NoOverlap_CoversEverySegmentExactlyOnce()
    {
        var segs = new[]
        {
            Sized("A", 0, 60), Sized("B", 1, 60), Sized("C", 2, 60), Sized("D", 3, 60),
        };

        var chunks = TranscriptChunker.Chunk(segs, targetChars: 100, overlapSegments: 0);

        // Concatenated chunk text contains each segment's text exactly once (no duplication, none dropped).
        var joined = string.Join("\n", chunks.Select(c => c.Text));
        foreach (var s in segs)
            Assert.Equal(1, CountOccurrences(joined, s.Text));
    }

    [Fact]
    public void Chunk_DoesNotEmitTrailingOverlapOnlyChunk()
    {
        // Split lands exactly on the last segment: there must be no extra chunk that only repeats the overlap.
        var segs = new[] { Sized("A", 0, 60), Sized("B", 1, 60) };

        var chunks = TranscriptChunker.Chunk(segs, targetChars: 100, overlapSegments: 1);

        var c = Assert.Single(chunks);
        Assert.Contains(segs[0].Text, c.Text);
        Assert.Contains(segs[1].Text, c.Text);
    }

    [Fact]
    public void Chunk_TimesAreMinStartAndMaxEnd_WithinChunk()
    {
        var chunks = TranscriptChunker.Chunk(
        [
            Seg("A", 500, 1500, "one"),
            Seg("B", 1400, 900_000, "two"), // out-of-order end (overlapping speech) — chunk end is the max
            Seg("A", 1600, 2000, "three"),
        ], targetChars: 10_000);

        var c = Assert.Single(chunks);
        Assert.Equal(500, c.StartMs);
        Assert.Equal(900_000, c.EndMs);
    }

    [Fact]
    public void Chunk_IgnoresBlankSpeakersInSpeakerSet()
    {
        var chunks = TranscriptChunker.Chunk(
        [
            Seg("", 0, 1000, "anon line"),
            Seg("Alice", 1000, 2000, "named line"),
        ], targetChars: 10_000);

        var c = Assert.Single(chunks);
        Assert.Equal(["Alice"], c.Speakers);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
