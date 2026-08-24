using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The <c>Segment.WordsJson</c> column's reader/writer. Null is load-bearing here: it is the
/// "this segment has no word timings" state that every pre-existing recording is in, and the state the
/// split endpoint refuses on.</summary>
public class SegmentWordsTests
{
    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        var words = new List<SegmentWord> { new("Hello", 1200, 1600), new("world", 1700, 2500) };
        Assert.Equal(words, SegmentWords.Parse(SegmentWords.Serialize(words)));
    }

    [Fact]
    public void Serialize_NullOrEmpty_IsNull()
    {
        // An empty array would read as "aligned to nothing", which is a different and useless state.
        Assert.Null(SegmentWords.Serialize(null));
        Assert.Null(SegmentWords.Serialize([]));
    }

    [Fact]
    public void Parse_NullOrGarbage_IsEmpty()
    {
        // A column value we cannot read must degrade to "cannot be split", never a 500 in the middle of
        // rendering a transcript.
        Assert.Empty(SegmentWords.Parse(null));
        Assert.Empty(SegmentWords.Parse("   "));
        Assert.Empty(SegmentWords.Parse("not json"));
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        // The app's global serializer is camelCase while the worker's contract is PascalCase. If reading
        // depended on which casing wrote the row, a mismatch would silently return an empty list - which
        // reads as "this segment has no words" rather than as an error.
        List<SegmentWord> expected = [new("Hi", 1, 2)];
        Assert.Equal(expected, SegmentWords.Parse("""[{"w":"Hi","s":1,"e":2}]"""));
        Assert.Equal(expected, SegmentWords.Parse("""[{"W":"Hi","S":1,"E":2}]"""));
    }

    [Fact]
    public void Serialize_UsesTheCompactKeys()
    {
        // These are stored per segment and a long meeting carries ~10k of them, so the key names are part
        // of the storage cost, not a cosmetic choice.
        Assert.Equal("""[{"w":"Hi","s":1,"e":2}]""", SegmentWords.Serialize([new("Hi", 1, 2)]));
    }
}
