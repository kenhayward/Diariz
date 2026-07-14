using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Resolves a template's <c>field</c> blocks against a recording. Shared by the minutes pipeline and the
/// formula run pipeline, so a `{{date}}` means the same thing wherever it appears - the two used to be able to
/// drift because only the minutes generator knew how to substitute anything.
///
/// The valid names are <see cref="TemplateContent.Fields"/>. A field that has no value resolves to null, and the
/// composer drops the block (and the section, if that empties it).</summary>
public static class TemplateFields
{
    public static string? Resolve(
        string name,
        DateTimeOffset? meetingDate,
        string? title,
        IReadOnlyList<string> attendees,
        long? durationMs,
        IReadOnlyList<ExtractedAction> actions,
        string? notesMarkdown) => name switch
    {
        "date" => meetingDate?.ToString("yyyy-MM-dd"),
        "time" => meetingDate?.ToString("HH:mm"),
        "title" => string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
        "attendees" => AttendeeFormatter.Summarize(attendees),
        "duration" => FormatDuration(durationMs),
        "action_items" => NullIfEmpty(MeetingMinutesPrompt.RenderActionItems(actions)),
        "notes" => notesMarkdown,
        _ => null,
    };

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? FormatDuration(long? ms)
    {
        if (ms is null or <= 0) return null;
        var t = TimeSpan.FromMilliseconds(ms.Value);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes}m {t.Seconds:D2}s";
    }
}
