using System.Text.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Reads and writes the <c>Segment.WordsJson</c> column.
///
/// <para>One place, one options object, on purpose. The app's global serializer is camelCase while the
/// worker's contract is PascalCase, so a hand-rolled Serialize/Deserialize pair written at a call site
/// could emit one casing and read the other - and the failure would be silent, since a mismatch
/// deserialises to an empty list, which is indistinguishable from "this segment has no words".</para></summary>
public static class SegmentWords
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <returns>Null for a null or empty list - null is the column's "no word timings" state, and the one
    /// the split endpoint refuses on.</returns>
    public static string? Serialize(IReadOnlyList<SegmentWord>? words) =>
        words is null || words.Count == 0 ? null : JsonSerializer.Serialize(words, Options);

    /// <summary>Never throws. An unreadable column value reads as "no words", which degrades to a segment
    /// that cannot be split rather than a 500 in the middle of rendering a transcript.</summary>
    public static IReadOnlyList<SegmentWord> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SegmentWord>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
