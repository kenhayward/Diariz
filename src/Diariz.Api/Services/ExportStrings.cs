namespace Diariz.Api.Services;

/// <summary>The localizable labels used when rendering a transcript to a downloadable document or email.
/// Plain data, so the formatters stay pure and unit-testable; resolved per request from the owner's UI
/// language by <see cref="IExportLocalizer"/>. <see cref="Subject"/> is a template containing
/// <c>{name}</c>.</summary>
public record ExportStrings(
    string TranscriptName,
    string Summary,
    string Actions,
    string Transcript,
    string Action,
    string Actor,
    string Deadline,
    string Time,
    string Speaker,
    string Text,
    string None,
    string SentFromDiariz,
    string Subject,
    string MeetingMinutes,
    string MinutesSubject)
{
    /// <summary>The authoritative English labels — the default when no localizer is supplied (keeps the
    /// formatters usable on their own) and the fallback for any key a translation omits.</summary>
    public static readonly ExportStrings English = new(
        TranscriptName: "Transcript Name",
        Summary: "Summary",
        Actions: "Actions",
        Transcript: "Transcript",
        Action: "Action",
        Actor: "Actor",
        Deadline: "Deadline",
        Time: "Time",
        Speaker: "Speaker",
        Text: "Text",
        None: "(none)",
        SentFromDiariz: "Sent from Diariz",
        Subject: "Transcript for {name}",
        MeetingMinutes: "Meeting Minutes",
        MinutesSubject: "Meeting minutes for {name}");

    /// <summary>The email subject for a recording, with <c>{name}</c> substituted.</summary>
    public string SubjectFor(string name) => Subject.Replace("{name}", name);

    /// <summary>The email subject for a recording's meeting minutes, with <c>{name}</c> substituted.</summary>
    public string MinutesSubjectFor(string name) => MinutesSubject.Replace("{name}", name);
}
