namespace Diariz.Domain.Entities;

/// <summary>One flattened occurrence of a desktop Outlook appointment, mirrored server-side.
///
/// <para>Outlook expands recurring series itself, so this is always a single occurrence - there is no master
/// row and no recurrence rule. Cancelled appointments are never stored: the desktop drops them, so the sync's
/// orphan sweep removes any copy that was already here.</para></summary>
public class OutlookCalendarEvent
{
    /// <summary>Deterministic id derived from <see cref="SourceId"/> + <see cref="Uid"/> (see
    /// <c>OutlookEventId.For</c>), <b>not</b> a random surrogate: an occurrence that drops out of the rolling
    /// window and later returns must come back as the same row, or every recording link and pre-meeting note
    /// pointing at it silently orphans.</summary>
    public Guid Id { get; set; }

    public Guid SourceId { get; set; }
    public OutlookCalendarSource? Source { get; set; }

    /// <summary>Denormalised owner, so the hot "events in this window for this user" read needs no join.
    /// Cascades from the user as well as from the source - the same double path <see cref="MeetingNote"/> and
    /// <see cref="MeetingScreenshot"/> already use.</summary>
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Outlook's <c>GlobalAppointmentID</c> - the per-occurrence Global Object ID, the same value EWS
    /// returns as <c>calendar:UID</c>. Stable across moves between folders and stores, and distinct per
    /// instance of a recurring series (Outlook stamps the occurrence date into it), which is why it beats
    /// <c>EntryID</c> (unstable) and <c>CleanGlobalObjectId</c> (identical for the whole series). Falls back to
    /// <c>entry:{sha1(EntryID)}</c> for local appointments that carry no Global Object ID.</summary>
    public string Uid { get; set; } = string.Empty;

    public string? Subject { get; set; }

    /// <summary>Absolute instants, always populated - including for all-day entries (local midnight converted
    /// to UTC) - so the window query, the ordering and the sweep have one sortable column whatever the kind.</summary>
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>A date-only entry (holiday, birthday, out-of-office day) rather than a timed meeting. Such an
    /// entry blankets the whole day, so calendar matching never picks one automatically.</summary>
    public bool AllDay { get; set; }

    /// <summary>For an all-day entry, the <b>local</b> calendar dates as <c>yyyy-MM-dd</c>; null for a timed
    /// meeting. These, not <see cref="StartsAt"/>, are the display truth. Deriving the date from the UTC
    /// instant is the classic off-by-one: an all-day entry on 2026-03-15 in Europe/London is the instant
    /// 2026-03-14T23:00:00Z, so slicing the ISO string yields the previous day for every summer date.
    /// <see cref="EndDate"/> is the exclusive next-day boundary, matching Google and iCalendar.</summary>
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }

    /// <summary>The appointment's time zone as an IANA id, converted server-side from
    /// <see cref="WindowsTimeZoneId"/>; falls back to the device's own zone rather than being left null.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>The raw Windows zone id Outlook reported (e.g. "GMT Standard Time"), kept for diagnosis when a
    /// conversion has no IANA mapping.</summary>
    public string? WindowsTimeZoneId { get; set; }

    public string? Location { get; set; }

    /// <summary>Teams/Zoom join link, when the appointment carries one. Doubles as the event's clickable target
    /// - unlike Google, a local Outlook item has no web permalink.</summary>
    public string? OnlineMeetingUrl { get; set; }

    /// <summary>The invite text, already converted to plain text on the user's machine - HTML is never
    /// transmitted (it carries tracking pixels and inline images) - and length-capped on write. Null when the
    /// source has <see cref="OutlookCalendarSource.IncludeBody"/> off, or the appointment is private.</summary>
    public string? BodyText { get; set; }

    /// <summary>Outlook categories, comma-joined as Outlook gives them.</summary>
    public string? Categories { get; set; }

    /// <summary>Outlook sensitivity: 0 Normal, 1 Personal, 2 Private, 3 Confidential. Append only - values
    /// persist as ints.</summary>
    public int Sensitivity { get; set; }

    /// <summary>Outlook busy status: 0 Free, 1 Tentative, 2 Busy, 3 Out of Office, 4 Working Elsewhere.
    /// Append only - values persist as ints.</summary>
    public int BusyStatus { get; set; }

    /// <summary>Whether this occurrence belongs to a recurring series (informational; occurrences are stored
    /// flat either way).</summary>
    public bool IsRecurring { get; set; }

    public string? OrganizerName { get; set; }
    public string? OrganizerEmail { get; set; }

    /// <summary>Attendees as JSON (<c>jsonb</c> on Postgres): name, email, response status, optional and
    /// organizer flags. Response statuses use Google's vocabulary (accepted/declined/tentative/needsAction) so
    /// the shared <c>CalendarAttendee</c> projection and every existing renderer work unchanged.</summary>
    public string? AttendeesJson { get; set; }

    /// <summary>Outlook's <c>LastModificationTime</c> - the change-detection fingerprint. A push whose value
    /// matches the stored one skips the field update entirely; there is no content hashing.</summary>
    public DateTimeOffset SourceLastModified { get; set; }

    /// <summary>Stamped with the current run's id by every upsert. The orphan sweep deletes in-window rows
    /// whose <see cref="SyncId"/> is stale, i.e. rows the latest complete run did not see - which is how a
    /// deleted or cancelled appointment disappears. Scoping is structural: this table holds only rows this
    /// sync created for this source, so unlike a mirror written into someone's real calendar there is nothing
    /// else the sweep could hit.</summary>
    public Guid SyncId { get; set; }

    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}
