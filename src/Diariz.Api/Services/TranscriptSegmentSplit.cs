using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) division of one transcript segment into two at a word boundary, so a block that
/// contains a second voice can be separated and the interloper reassigned. Mirrors
/// <see cref="TranscriptSegmentMerge"/>'s separation of arithmetic from persistence.
///
/// <para>The cut snaps to a word because the halves feed voiceprint training: a boundary estimated from
/// the text would hand a slice of the wrong person's audio to whichever half is later enrolled.</para>
///
/// <para><b>Each half's text is cut out of the segment's own <c>Original</c>, not rebuilt by joining its
/// words with spaces.</b> A merged block carries line breaks between the parts it swallowed, and rejoining
/// would silently reflow it to one line. Joining is only the fallback for when the words no longer describe
/// the text at all.</para></summary>
public static class TranscriptSegmentSplit
{
    /// <param name="Words">The words that fell on this side. Never empty - a half of a splittable segment
    /// always has at least one word.</param>
    public record Half(string Text, long StartMs, long EndMs, IReadOnlyList<SegmentWord> Words);

    public record Result(Half Left, Half Right);

    /// <summary>Split before <paramref name="wordIndex"/>.</summary>
    /// <param name="startMs">The segment's own start, kept as the left half's start so the transcript's
    /// timestamps do not shift under a user who only divided a row.</param>
    /// <param name="endMs">The segment's own end, kept as the right half's end for the same reason.</param>
    /// <returns>Null when the index would leave a half empty, or when there are fewer than two words -
    /// there is nothing to divide.</returns>
    public static Result? Split(long startMs, long endMs, string original,
        IReadOnlyList<SegmentWord> words, int wordIndex)
    {
        if (words.Count < 2 || wordIndex < 1 || wordIndex >= words.Count) return null;

        var leftWords = words.Take(wordIndex).ToList();
        var rightWords = words.Skip(wordIndex).ToList();

        var cut = TextCutOffset(original, words, wordIndex);
        var (leftText, rightText) = cut is int at
            ? (original[..at].TrimEnd(), original[at..].TrimStart())
            // The words and the text have drifted apart. Dividing the words correctly still beats
            // refusing, so rebuild each side's text from the words that actually went to it.
            : (string.Join(' ', leftWords.Select(w => w.W)), string.Join(' ', rightWords.Select(w => w.W)));

        return new Result(
            // The gap between the two words belongs to neither half: it is where the other voice starts.
            new Half(leftText, startMs, leftWords[^1].E, leftWords),
            new Half(rightText, rightWords[0].S, endMs, rightWords));
    }

    /// <summary>Character offset in <paramref name="original"/> where the word at
    /// <paramref name="wordIndex"/> begins, found by walking the words in order from a moving cursor.
    /// Searching for each word independently would match an earlier, unrelated occurrence of it.</summary>
    /// <returns>Null when any word up to the index is not found in order, which means the words no longer
    /// describe this text.</returns>
    public static int? TextCutOffset(string original, IReadOnlyList<SegmentWord> words, int wordIndex)
    {
        if (wordIndex < 1 || wordIndex >= words.Count) return null;

        var cursor = 0;
        for (var i = 0; i <= wordIndex; i++)
        {
            if (cursor > original.Length) return null;
            var at = original.IndexOf(words[i].W, cursor, StringComparison.Ordinal);
            if (at < 0) return null;
            if (i == wordIndex) return at;
            cursor = at + words[i].W.Length;
        }
        return null;
    }
}
