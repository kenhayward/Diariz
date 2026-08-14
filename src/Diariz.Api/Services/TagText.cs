using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>The one place that decides what a tag may look like. A tag written through this normaliser never
/// contains whitespace: internal whitespace collapses to a hyphen so a pasted phrase becomes one token
/// ("budget planning 2026" -> "budget-planning-2026"). Case is preserved deliberately, whether the tag is a
/// machine suggestion or typed by hand - only case is folded for comparison, never the words themselves.
/// A row written before extraction started calling this on suggestions can still hold un-normalised text
/// with a space in it ("Data Collection"), so callers must never assume a stored tag is already normalised:
/// every lookup normalises both sides again before comparing, case-insensitively - which is also what lets
/// an old spaced row and a new hyphenated one match as the same tag.
/// Mirrored in TypeScript by <c>apps/web/src/lib/tagInput.ts</c>; change both together.</summary>
public static partial class TagText
{
    /// <summary>Longest tag the <c>RecordingTags.Tag</c> column holds.</summary>
    public const int MaxLength = 64;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Cleans a raw tag, or returns null when nothing usable is left (blank, or hyphens only).</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var joined = Whitespace().Replace(raw.Trim(), "-").Trim('-');
        if (joined.Length == 0) return null;

        return joined.Length > MaxLength ? joined[..MaxLength] : joined;
    }
}
