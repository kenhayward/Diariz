using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>Formats a recording's attendee display names for the template's <c>attendees</c> field. Identified
/// people are listed by name; the auto-labelled diarization speakers still on their default placeholder label -
/// <c>SPEAKER_nn</c> or <c>UNKNOWN</c> (a segment with no attributed speaker) - collapse to a count (e.g. "Alice,
/// Bob and 11 unidentified attendees") so the minutes don't spell out a long list of anonymous speakers. Returns
/// null when there are no attendees at all.</summary>
public static partial class AttendeeFormatter
{
    [GeneratedRegex(@"^(?:SPEAKER_\d+|UNKNOWN)$")]
    private static partial Regex UnidentifiedLabel();

    public static string? Summarize(IReadOnlyList<string>? attendees)
    {
        var names = (attendees ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();
        if (names.Count == 0) return null;

        var identified = names.Where(n => !UnidentifiedLabel().IsMatch(n)).ToList();
        var unidentified = names.Count - identified.Count;

        if (unidentified == 0) return string.Join(", ", identified);

        var tail = $"{unidentified} unidentified {(unidentified == 1 ? "attendee" : "attendees")}";
        return identified.Count == 0 ? tail : $"{string.Join(", ", identified)} and {tail}";
    }
}
