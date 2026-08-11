using System.Globalization;

namespace Diariz.OutlookReader;

/// <summary>How much of the calendar to read, and what the user has allowed to leave the machine.</summary>
public sealed class ReaderOptions
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool SkipPrivate { get; init; } = true;
    public bool IncludeBody { get; init; } = true;
    public int Max { get; init; } = 5000;
    /// <summary>Bodies are cut here before leaving the machine, not on arrival.</summary>
    public int MaxBodyChars { get; init; } = 4000;

    /// <summary>Answer "is classic Outlook installed?" from the registry and exit, without creating the COM
    /// object. The shell asks this before it ever offers the connector - see
    /// <see cref="OutlookPresence"/> for why touching COM is the thing to avoid.</summary>
    public bool Probe { get; init; }

    public static ReaderOptions Parse(string[] args)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : null;
            map[key] = value;
        }

        var now = DateTime.Now;
        return new ReaderOptions
        {
            // Local times, because that is what Outlook's Restrict filter compares against.
            Start = Date(map, "start") ?? now.AddDays(-30),
            End = Date(map, "end") ?? now.AddDays(180),
            // Flags are opt-out: their absence means the safer default stays.
            SkipPrivate = !map.ContainsKey("include-private"),
            IncludeBody = !map.ContainsKey("no-body"),
            Max = Int(map, "max") ?? 5000,
            MaxBodyChars = Int(map, "max-body") ?? 4000,
            Probe = map.ContainsKey("probe"),
        };
    }

    private static DateTime? Date(IReadOnlyDictionary<string, string?> map, string key) =>
        map.TryGetValue(key, out var raw) && DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().DateTime
            : null;

    private static int? Int(IReadOnlyDictionary<string, string?> map, string key) =>
        map.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : null;
}

/// <summary>The single JSON document written to stdout.</summary>
public sealed class ReadResult
{
    public bool Ok { get; set; }
    public string? MailboxName { get; set; }
    public string? DeviceTimeZone { get; set; }

    /// <summary>False when enumeration threw partway. The server refuses to delete anything it was not told
    /// about when this is false, which is what stops one failed COM call wiping a mirrored calendar.</summary>
    public bool Complete { get; set; } = true;

    /// <summary>A machine-readable failure: <c>not-installed</c>, <c>new-outlook</c>, <c>busy</c>,
    /// <c>denied</c>, <c>error</c>. The shell turns this into the message the user sees.</summary>
    public string? Reason { get; set; }
    public string? Error { get; set; }

    public List<EventDto> Events { get; set; } = [];

    public static ReadResult Failed(string reason, string error) =>
        new() { Ok = false, Complete = false, Reason = reason, Error = error };
}

public sealed class EventDto
{
    public string Uid { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public bool AllDay { get; set; }
    /// <summary>Local <c>yyyy-MM-dd</c> for an all-day entry - the display truth, never re-derived from the
    /// UTC instant.</summary>
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string? WindowsTimeZoneId { get; set; }
    public string? Location { get; set; }
    public string? OnlineMeetingUrl { get; set; }
    public string? BodyText { get; set; }
    public string? Categories { get; set; }
    public int Sensitivity { get; set; }
    public int BusyStatus { get; set; }
    public bool IsRecurring { get; set; }
    public string? OrganizerName { get; set; }
    public string? OrganizerEmail { get; set; }
    public List<AttendeeDto> Attendees { get; set; } = [];
    public DateTimeOffset? LastModified { get; set; }
}

public sealed class AttendeeDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    /// <summary>Google's vocabulary (accepted/declined/tentative/needsAction), mapped from Outlook's numeric
    /// status here so nothing downstream has to translate.</summary>
    public string? Response { get; set; }
    public bool Optional { get; set; }
}
