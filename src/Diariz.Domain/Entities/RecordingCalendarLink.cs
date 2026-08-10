namespace Diariz.Domain.Entities;

/// <summary>A persisted link from a <see cref="Recording"/> to the Google Calendar event it belongs to
/// (1:1 with the recording, shared primary key). Stores a lightweight snapshot of the event so the
/// recordings list and Calendar tab can show the calendar icon and dedupe cheaply without a live Google
/// call. The rich invite details (attendees/description/location) are always fetched live by
/// <see cref="EventId"/>, never stored here (so they can't go stale).</summary>
public class RecordingCalendarLink
{
    /// <summary>Primary key and FK to the owning <see cref="Recording"/> (cascade-deleted with it).</summary>
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>The Google Calendar event id.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>The id of the calendar the event lives on - "primary", or a secondary/shared/subscribed
    /// calendar id. Lets the live re-fetch and preview target the right calendar. Existing rows backfill to
    /// "primary".</summary>
    public string CalendarId { get; set; } = "primary";

    /// <summary>The recurring series this occurrence belongs to, or null for a one-off (and for links made
    /// before this column existed whose id shape could not be read). Keyed per source - a Google master id, an
    /// <c>.ics</c> UID, an Outlook Global Object ID - so it is only ever compared with another link's, never
    /// interpreted.
    /// <para>Stored rather than derived on read because Outlook's mirror is a rolling window: a join to
    /// <c>OutlookCalendarEvents</c> would find nothing for last month's occurrence, which is precisely the
    /// history the series list exists to show.</para></summary>
    public string? SeriesId { get; set; }

    /// <summary>The calendar's Google background colour (hex), snapshotted so the linked icon can be coloured
    /// without a live call. Null = unknown.</summary>
    public string? Color { get; set; }

    /// <summary>Snapshot of the event title at link time (may be stale; the UI refreshes from a live fetch).</summary>
    public string? Summary { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>The Google Calendar deep link to the event (opens it in the user's calendar).</summary>
    public string? HtmlLink { get; set; }

    /// <summary>True when the user picked this event by hand (vs. an auto-saved best time-overlap match).
    /// Manual links are never overwritten by the auto-match.</summary>
    public bool LinkedManually { get; set; }

    /// <summary>When the snapshot was last written from Google (link/relink time).</summary>
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}
