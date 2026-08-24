using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The pure arithmetic of dividing one segment in two at a word boundary.</summary>
public class TranscriptSegmentSplitTests
{
    private static readonly IReadOnlyList<SegmentWord> Words =
    [
        new("Hello", 1000, 1400),
        new("world", 1500, 1900),
        new("again", 2100, 2500),
    ];

    [Fact]
    public void Split_DividesTextAndWordsAtTheIndex()
    {
        var r = TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, 2)!;

        Assert.Equal("Hello world", r.Left.Text);
        Assert.Equal("again", r.Right.Text);
        Assert.Equal(2, r.Left.Words.Count);
        Assert.Single(r.Right.Words);
    }

    [Fact]
    public void Split_LeavesTheInterWordGapInNeitherHalf()
    {
        // The silence between two speakers' words belongs to neither of them. Including it would put a
        // slice of the interloper's audio into whichever half later trains a voiceprint - which is the
        // whole reason the cut snaps to a word rather than being estimated.
        var r = TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, 2)!;

        Assert.Equal(1900, r.Left.EndMs);    // end of "world"
        Assert.Equal(2100, r.Right.StartMs); // start of "again"
    }

    [Fact]
    public void Split_KeepsTheOuterBoundsOfTheOriginalSegment()
    {
        // The left half must still start where the row started, or the transcript's timestamps shift under
        // a user who only divided a row.
        var r = TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, 2)!;

        Assert.Equal(900, r.Left.StartMs);
        Assert.Equal(2600, r.Right.EndMs);
    }

    [Fact]
    public void Split_PreservesNewlinesInsideAMergedBlock()
    {
        // A merged block joins its parts with a line break. Rebuilding each half by joining words with
        // spaces would silently reflow it to one line - an edit the user never asked for, in a feature
        // whose entire point is precision.
        IReadOnlyList<SegmentWord> words =
            [new("Hello", 0, 500), new("world", 600, 900), new("again", 1000, 1400)];
        var r = TranscriptSegmentSplit.Split(0, 1500, "Hello\nworld again", words, 2)!;

        Assert.Equal("Hello\nworld", r.Left.Text);
        Assert.Equal("again", r.Right.Text);
    }

    [Fact]
    public void Split_PreservesPunctuationAttachedToWords()
    {
        IReadOnlyList<SegmentWord> words = [new("Hello,", 0, 500), new("world.", 600, 900)];
        var r = TranscriptSegmentSplit.Split(0, 1000, "Hello, world.", words, 1)!;

        Assert.Equal("Hello,", r.Left.Text);
        Assert.Equal("world.", r.Right.Text);
    }

    [Fact]
    public void Split_FallsBackToJoiningWhenTheWordsNoLongerMatchTheText()
    {
        // Defensive. If text and words have drifted apart, dividing the words correctly still beats
        // refusing, and each half's text is rebuilt from the words that actually went to it.
        var r = TranscriptSegmentSplit.Split(0, 1000, "completely unrelated text", Words, 2)!;

        Assert.Equal("Hello world", r.Left.Text);
        Assert.Equal("again", r.Right.Text);
    }

    [Theory]
    [InlineData(0)]   // nothing would be left on the left
    [InlineData(3)]   // nothing would be left on the right
    [InlineData(-1)]
    [InlineData(99)]
    public void Split_OutOfRangeIndex_IsNull(int index) =>
        Assert.Null(TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, index));

    [Fact]
    public void Split_WithFewerThanTwoWords_IsNull() =>
        Assert.Null(TranscriptSegmentSplit.Split(0, 100, "Hi", [new SegmentWord("Hi", 0, 100)], 1));

    [Fact]
    public void Split_WithNoWords_IsNull() =>
        Assert.Null(TranscriptSegmentSplit.Split(0, 100, "Hi", [], 1));

    [Fact]
    public void TextCutOffset_IsTheStartOfTheWordAtTheIndex()
    {
        Assert.Equal(6, TranscriptSegmentSplit.TextCutOffset("Hello world again", Words, 1));
        Assert.Equal(12, TranscriptSegmentSplit.TextCutOffset("Hello world again", Words, 2));
    }

    [Fact]
    public void TextCutOffset_WhenAWordIsAbsent_IsNull() =>
        Assert.Null(TranscriptSegmentSplit.TextCutOffset("Hello there again", Words, 2));

    [Fact]
    public void TextCutOffset_MatchesInOrderNotAnywhere()
    {
        // "again" appears before "world" in this text. Walking from a moving cursor keeps the words in
        // their real order; searching each word independently would cut at the earlier, wrong occurrence.
        Assert.Null(TranscriptSegmentSplit.TextCutOffset("Hello again world", Words, 2));
    }

    [Fact]
    public void Split_ProducesHalvesThatStillReadInOrder()
    {
        // Every boundary must stay monotonic, or the transcript renders out of order and the audio
        // scrubber seeks backwards.
        var r = TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, 1)!;

        Assert.True(r.Left.StartMs <= r.Left.EndMs);
        Assert.True(r.Left.EndMs <= r.Right.StartMs);
        Assert.True(r.Right.StartMs <= r.Right.EndMs);
    }
}
