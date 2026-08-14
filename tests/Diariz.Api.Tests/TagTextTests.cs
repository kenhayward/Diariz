using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TagTextTests
{
    /// U+00A0. Built from its code point rather than written as a literal or an escape: an invisible
    /// character in a source literal is unreviewable, and the TypeScript mirror does the same.
    private static readonly string Nbsp = ((char)0xA0).ToString();
    private static readonly string A62 = new('a', 62);
    private static readonly string A63 = new('a', 63);
    private static readonly string A64 = new('a', 64);

    /// <summary>The normalisation contract, as a table shared VERBATIM with the TypeScript mirror: the same
    /// input/expected pairs live in <c>SHARED_FIXTURE</c> in <c>apps/web/src/lib/tagInput.test.ts</c>. The two
    /// implementations (<see cref="TagText.Normalize"/> and <c>normalizeTag</c>) mirror each other by
    /// convention only - nothing compiles them together - and a drift between them surfaces to the user as a
    /// chip that re-spells itself after the refetch, or as a 500 from a duplicate insert. Keeping one table on
    /// each side means a future divergence fails a test instead. Add a case to BOTH lists or to neither.
    ///
    /// Two known asymmetries are deliberately absent, because the two regex engines genuinely disagree and
    /// neither behaviour is worth changing:
    ///   * U+0085 (NEL) - .NET's <c>\s</c> matches it, JavaScript's does not.
    ///   * U+FEFF (BOM / zero-width no-break space) - JavaScript's <c>\s</c> matches it, .NET's does not.
    /// Both are accepted, documented divergences. A null input is also absent: this method takes a
    /// <c>string?</c> while the TypeScript one takes a <c>string</c>, so that case is covered separately in
    /// <see cref="Normalize_ReturnsNull_WhenThereIsNoUsableText"/> - a signature difference, not a behaviour
    /// difference.</summary>
    public static TheoryData<string, string?> SharedFixture => new()
    {
        // Plain words, case preserved either way.
        { "metadata", "metadata" },
        { "  metadata  ", "metadata" },
        { "Roadmap", "Roadmap" },
        { "iOS", "iOS" },

        // Whitespace inside becomes a hyphen, so a phrase lands as one token.
        { "Data Collection", "Data-Collection" },
        { "budget planning 2026", "budget-planning-2026" },
        { "many   spaces", "many-spaces" },
        { "spaced\tout\nword", "spaced-out-word" },
        { "line\r\nbreak", "line-break" },
        { Nbsp + "nbsp" + Nbsp, "nbsp" },   // a non-breaking space (pasted from a doc) is whitespace to both

        // Edge hyphens go, however many.
        { "-leading", "leading" },
        { "trailing-", "trailing" },
        { "--both--", "both" },

        // Nothing usable left.
        { "", null },
        { "   ", null },
        { "\t\n ", null },
        { "-", null },
        { "---", null },

        // Truncation to the column length - and the hyphen trim has to run AGAIN after the slice, or the
        // result is not something Normalize would leave alone. A tag whose 64th character ends up a hyphen
        // used to keep it, which made the function non-idempotent: re-adding such a tag missed the existing
        // row, inserted, and tripped the unique index as an unhandled 500, while adopting an over-long
        // suggestion inserted a second row and the chip visibly re-spelled itself after the refetch.
        { new string('x', 100), new string('x', 64) },
        { A64 + "bbb", A64 },
        { A63 + "-bbbb", A63 },          // hyphen exactly at index 63
        { A63 + " bbbb", A63 },          // whitespace at index 63 -> hyphen -> trimmed
        { A63 + "-", A63 },
        { A62 + "--bbb", A62 },          // more than one hyphen at the boundary
    };

    [Theory]
    [MemberData(nameof(SharedFixture))]
    public void Normalize_MatchesTheSharedFixture(string raw, string? expected) =>
        Assert.Equal(expected, TagText.Normalize(raw));

    /// <summary>The property behind the truncation cases above: a normalised tag is already normalised.
    /// Every lookup normalises both sides before comparing (see <c>FindAllByNormalizedTag</c>), so a value
    /// Normalize would change again can never be found by the text it was stored as.</summary>
    [Theory]
    [MemberData(nameof(SharedFixture))]
    public void Normalize_IsIdempotent(string raw, string? expected)
    {
        var once = TagText.Normalize(raw);
        Assert.Equal(expected, once);
        Assert.Equal(once, TagText.Normalize(once));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("---")]
    public void Normalize_ReturnsNull_WhenThereIsNoUsableText(string? raw) =>
        Assert.Null(TagText.Normalize(raw));

    [Fact]
    public void Normalize_NeverExceedsTheColumnLength() =>
        Assert.Equal(TagText.MaxLength, TagText.Normalize(new string('x', 100))!.Length);
}
