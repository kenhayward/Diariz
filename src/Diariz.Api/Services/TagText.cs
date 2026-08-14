using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>The one place that decides what a tag may look like. A tag never contains whitespace: internal
/// whitespace collapses to a hyphen so a pasted phrase becomes one token ("budget planning 2026" ->
/// "budget-planning-2026"). Case is preserved deliberately - suggestions arrive in the extraction prompt's
/// Title Case and hand-typed tags stay as typed, while every comparison and the tag cloud are
/// case-insensitive, so the two styles coexist without a data migration.
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
