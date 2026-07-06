using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Seeds the standard Platform meeting types (minutes templates) the app ships with. Idempotent by
/// <see cref="MeetingType.Key"/> and <b>insert-if-missing</b> - it never overwrites an existing type, so a
/// Platform Administrator's edits to (or deletion of) a standard survive redeploys. Runs on every boot.</summary>
public static class MeetingTypeSeeder
{
    // ---- content-building helpers (keep the template definitions readable) ----
    private static TemplateBlock Text(string s) => new(TemplateBlock.Boilerplate, Text: s);
    private static TemplateBlock Field(string f) => new(TemplateBlock.FieldKind, Field: f);
    private static TemplateBlock Prompt(string s) => new(TemplateBlock.Prompt, Text: s);
    private static TemplateSection Sec(int level, string title, params TemplateBlock[] blocks) =>
        new(level, title, blocks);
    private static string Content(params TemplateSection[] sections) =>
        new MeetingTypeContent(sections).Serialize();

    /// <summary>The "General Meeting" default - reproduces the original minutes structure (metadata, purpose,
    /// themed discussion, decisions, open questions, next steps) and ends with the canonical actions table.</summary>
    private static string GeneralContent() => Content(
        Sec(1, "Meeting details",
            Text("Date: "), Field("date"),
            Text("Time: "), Field("time"),
            Text("Attendees: "), Field("attendees"),
            Text("Duration: "), Field("duration")),
        Sec(1, "Purpose",
            Prompt("State the purpose / context of the meeting in 1-2 lines.")),
        Sec(1, "Discussion",
            Prompt("Summarise the discussion grouped by theme (not chronologically), concise and decision-oriented. Omit this section if there was no substantive discussion.")),
        Sec(1, "Decisions",
            Prompt("List the decisions made. Omit this section if none were made.")),
        Sec(1, "Open questions",
            Prompt("List any open questions or parking-lot items. Omit this section if there are none.")),
        Sec(1, "Next steps",
            Prompt("Describe the next steps or next meeting in narrative form. Omit this section if there are none.")),
        Sec(1, "Action items",
            Field("action_items")));

    /// <summary>The standard set. Each is a Platform type (<see cref="MeetingType.UserId"/> = null).</summary>
    public static IReadOnlyList<MeetingType> Standards { get; } =
    [
        Std(MeetingType.GeneralKey, "Standard", "General Meeting", "document", "#5C6BC0",
            "A general-purpose meeting. Produce neutral, professional minutes suitable for forwarding.",
            GeneralContent()),

        Std("customer", "Customer", "Customer Meeting", "handshake", "#0B8043",
            "A meeting with an external customer or client. Capture their needs, commitments made, and follow-ups; keep the tone suitable for sharing back with the customer.",
            Content(
                Sec(1, "Meeting details", Text("Date: "), Field("date"), Text("Attendees: "), Field("attendees")),
                Sec(1, "Customer context", Prompt("Summarise the customer's situation, goals, and any concerns they raised.")),
                Sec(1, "Discussion", Prompt("Summarise what was discussed, grouped by topic.")),
                Sec(1, "Commitments", Prompt("List what each side agreed to do. Omit if none.")),
                Sec(1, "Action items", Field("action_items")))),

        Std("cadence-call", "Team", "Cadence Call", "refresh", "#F09300",
            "A recurring team cadence / stand-up. Focus on progress, blockers, and what's next; keep it terse.",
            Content(
                Sec(1, "Cadence call", Text("Date: "), Field("date"), Text("Attendees: "), Field("attendees")),
                Sec(1, "Progress", Prompt("Summarise progress reported since the last cadence, grouped by workstream or person.")),
                Sec(1, "Blockers", Prompt("List blockers or risks raised. Omit if none.")),
                Sec(1, "Action items", Field("action_items")))),

        Std("weekly-meeting", "Team", "Weekly Meeting", "calendar", "#3F51B5",
            "A weekly team meeting. Capture updates, decisions, and follow-ups.",
            Content(
                Sec(1, "Weekly meeting", Text("Date: "), Field("date"), Text("Attendees: "), Field("attendees")),
                Sec(1, "Updates", Prompt("Summarise the updates shared, grouped by topic or person.")),
                Sec(1, "Decisions", Prompt("List decisions made. Omit if none.")),
                Sec(1, "Action items", Field("action_items")))),

        Std("one-to-one", "Team", "1:1", "user", "#8E24AA",
            "A one-to-one conversation. Keep it private and constructive; capture themes and agreed actions, not verbatim remarks.",
            Content(
                Sec(1, "1:1", Text("Date: "), Field("date"), Text("Participants: "), Field("attendees")),
                Sec(1, "Topics", Prompt("Summarise the topics discussed at a high level, in neutral language.")),
                Sec(1, "Agreed actions", Prompt("List what was agreed. Omit if none.")),
                Sec(1, "Action items", Field("action_items")))),

        Std("interview", "Hiring", "Interview", "clipboard", "#D81B60",
            "A candidate interview. Summarise the candidate's responses and the interviewer's assessment objectively; avoid protected-characteristic commentary.",
            Content(
                Sec(1, "Interview", Text("Date: "), Field("date"), Text("Panel: "), Field("attendees")),
                Sec(1, "Discussion", Prompt("Summarise the topics covered and the candidate's key responses.")),
                Sec(1, "Assessment", Prompt("Summarise strengths and areas of concern raised, objectively and professionally.")),
                Sec(1, "Action items", Field("action_items")))),

        Std("town-hall", "Company", "Town Hall", "megaphone", "#039BE5",
            "An all-hands / town-hall. Capture announcements, key messages, and Q&A themes for a broad audience.",
            Content(
                Sec(1, "Town hall", Text("Date: "), Field("date")),
                Sec(1, "Announcements", Prompt("Summarise the announcements and key messages delivered.")),
                Sec(1, "Q&A", Prompt("Summarise the questions asked and answers given, grouped by theme. Omit if none.")),
                Sec(1, "Action items", Field("action_items")))),

        Std("webinar", "Company", "Webinar", "video", "#E67C73",
            "A webinar or presentation. Summarise the content presented and audience questions for attendees who missed it.",
            Content(
                Sec(1, "Webinar", Text("Date: "), Field("date")),
                Sec(1, "Overview", Prompt("Summarise the topic and the main points presented.")),
                Sec(1, "Key takeaways", Prompt("List the key takeaways in a few bullets.")),
                Sec(1, "Q&A", Prompt("Summarise audience questions and answers. Omit if none.")),
                Sec(1, "Action items", Field("action_items")))),
    ];

    private static MeetingType Std(
        string key, string group, string title, string icon, string color, string overview, string contentJson) =>
        new()
        {
            Key = key,
            UserId = null,
            GroupName = group,
            Title = title,
            Icon = icon,
            Color = color,
            Overview = overview,
            ContentJson = contentJson,
        };

    /// <summary>Insert any standard whose <see cref="MeetingType.Key"/> isn't already present. Existing rows are
    /// left untouched, so admin edits/deletions of a standard persist across redeploys.</summary>
    public static async Task SeedAsync(DiarizDbContext db, CancellationToken ct = default)
    {
        var existing = await db.MeetingTypes
            .Where(m => m.Key != null)
            .Select(m => m.Key!)
            .ToListAsync(ct);
        var have = existing.ToHashSet();

        foreach (var std in Standards.Where(s => !have.Contains(s.Key!)))
        {
            db.MeetingTypes.Add(new MeetingType
            {
                Id = Guid.NewGuid(),
                Key = std.Key,
                UserId = null,
                GroupName = std.GroupName,
                Title = std.Title,
                Icon = std.Icon,
                Color = std.Color,
                Overview = std.Overview,
                ContentJson = std.ContentJson,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
