namespace Diariz.Domain.Entities;

/// <summary>An external iCalendar (<c>.ics</c>) feed the user has subscribed to - a public team/shared calendar
/// or any ICS URL not reachable through their Google account. Its events are fetched live at read time (never
/// stored) and merged into the Calendar views alongside the user's Google calendars, tagged
/// <c>ics:{Id}</c> and coloured with <see cref="Color"/>. A user may hold several feeds; removing one deletes
/// the row (cascade with the user). The URL is SSRF-guarded before every fetch.</summary>
public class IcsCalendarSource
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    /// <summary>User-supplied label shown in the Calendar views, e.g. "Team calendar".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The feed URL (validated https only). Fetched server-side, so it is SSRF-checked on every read.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Hex colour (e.g. <c>#7986CB</c>) used to tint this feed's events, mirroring Google's per-calendar
    /// colours. Null falls back to a default feed colour in the UI.</summary>
    public string? Color { get; set; }

    /// <summary>Whether this feed's events are shown/merged. Off = kept but excluded from reads.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the feed was last fetched successfully. Null until first read.</summary>
    public DateTimeOffset? LastFetchedAt { get; set; }

    /// <summary>The last fetch error (unreachable host, non-200, too large, parse failure), surfaced in the UI so
    /// the user can fix a broken feed. Null when the last fetch succeeded.</summary>
    public string? LastError { get; set; }
}
