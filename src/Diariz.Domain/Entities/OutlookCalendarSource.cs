namespace Diariz.Domain.Entities;

/// <summary>One machine's connection to a local desktop Outlook calendar.
///
/// <para>Unlike the user's Google calendars and their <see cref="IcsCalendarSource"/> feeds - both of which the
/// API fetches live at read time and never stores - Outlook lives on the user's PC and is only reachable by the
/// Windows desktop app. So its events are <b>pushed</b> to the API and <b>persisted</b>
/// (<see cref="OutlookCalendarEvent"/>), which is what lets them keep working in a browser and after the
/// desktop app is closed.</para>
///
/// <para>Keyed <b>per device</b>, not per user: someone running the desktop app on two PCs against two
/// mailboxes needs two independent mirrors, or each machine's orphan sweep would delete the other's events on
/// every launch. Deleting a source cascades away its stored events.</para></summary>
public class OutlookCalendarSource
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>Opaque per-installation id, minted once by the desktop app and kept in its local config.
    /// Unique with <see cref="UserId"/> - a repeat push from the same machine updates its own source rather
    /// than creating another.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>The machine's hostname, for telling two connected devices apart in Preferences. Display only.</summary>
    public string? DeviceName { get; set; }

    /// <summary>The Outlook default account's address, so a user syncing a work and a personal mailbox can see
    /// which is which. Display only; never logged.</summary>
    public string? MailboxName { get; set; }

    /// <summary>User-editable label shown in the Calendar views, defaulting to "Outlook ({DeviceName})".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Hex colour used to tint this source's events, mirroring Google's per-calendar colours and
    /// <see cref="IcsCalendarSource.Color"/>. Null falls back to a default in the UI.</summary>
    public string? Color { get; set; }

    /// <summary>Whether this device's events are shown and merged. Off = kept but excluded from reads, exactly
    /// like <see cref="IcsCalendarSource.Enabled"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far back and forward the desktop reads, in days. The rolling window is what the orphan
    /// sweep is bounded by, so narrowing it never deletes events outside the new window. Clamped server-side.</summary>
    public int PastDays { get; set; } = 30;
    public int FutureDays { get; set; } = 180;

    /// <summary>Skip appointments marked Private or Confidential. On by default; the desktop drops them before
    /// they leave the machine rather than the server discarding them on arrival.</summary>
    public bool SkipPrivate { get; set; } = true;

    /// <summary>Store the invite body. When off the desktop sends none; a private appointment's body is
    /// stripped regardless of this flag.</summary>
    public bool IncludeBody { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this device last completed a push. Null until the first one.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>When this device last completed a <b>narrow</b> push - a day or less, the desktop's "Sync
    /// today". Stamped apart from <see cref="LastSyncedAt"/> so the two cooldowns do not block each other: a
    /// quick sync is what the user reaches for seconds after a full one has run, and sharing a stamp would
    /// refuse it exactly then. Null until the first quick sync.</summary>
    public DateTimeOffset? LastNarrowSyncedAt { get; set; }

    /// <summary>The last sync failure, surfaced in Preferences so a user can see a broken connector from any
    /// device (Outlook not installed, the new Outlook, a blocked COM call). Null when the last sync succeeded.
    /// Mirrors <see cref="IcsCalendarSource.LastError"/>.</summary>
    public string? LastError { get; set; }

    /// <summary>How many events the last completed sync left stored, for the "142 events, synced 3 minutes ago"
    /// line in Preferences.</summary>
    public int LastEventCount { get; set; }

    public ICollection<OutlookCalendarEvent> Events { get; set; } = new List<OutlookCalendarEvent>();
}
