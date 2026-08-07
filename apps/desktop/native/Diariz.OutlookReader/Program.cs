using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diariz.OutlookReader;

/// <summary>Reads a window of the local classic Outlook calendar and writes it to stdout as one JSON document.
///
/// <para>A separate process rather than a native Node addon on purpose. The Electron shell has no native
/// modules and this keeps it that way; a COM read of a busy calendar takes tens of seconds, which would freeze
/// the tray, the recorder IPC and the screenshot hotkey if it ran on the main process's event loop; and a
/// process boundary means a hung or crashing COM call kills this, not Diariz.</para>
///
/// <para>Late binding only - <c>Type.GetTypeFromProgID</c> plus <c>InvokeMember</c>, no interop assembly - so
/// there is no coupling to a particular Outlook version.</para></summary>
public static class Program
{
    // Outlook enum values, hard-coded rather than referenced, since there is no interop assembly.
    private const int OlFolderCalendar = 9;

    public static int Main(string[] args)
    {
        try
        {
            var options = ReaderOptions.Parse(args);
            var result = Read(options);
            Write(result);
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            // Any escape still produces a well-formed document: the shell parses stdout and would otherwise
            // have nothing to explain to the user.
            Write(ReadResult.Failed(Classify(ex), ex.Message));
            return 1;
        }
    }

    private static ReadResult Read(ReaderOptions options)
    {
        var progId = Type.GetTypeFromProgID("Outlook.Application");
        if (progId is null)
            return ReadResult.Failed("not-installed", "Outlook is not installed on this PC.");

        object? app = null;
        object? session = null;
        object? folder = null;

        try
        {
            app = CreateApplication();
            if (app is null)
                return ReadResult.Failed("not-installed", "Outlook could not be started.");

            // The new Outlook registers enough to create the Application object but has no MAPI behind it, so
            // this is where it fails rather than at creation. It is the case most users will hit as Microsoft
            // migrates them, so it gets its own reason rather than a generic error.
            try
            {
                session = Invoke(app, "GetNamespace", "MAPI");
                folder = Invoke(session!, "GetDefaultFolder", OlFolderCalendar);
            }
            catch (Exception ex)
            {
                return ReadResult.Failed("new-outlook", Describe(ex));
            }
            if (folder is null)
                return ReadResult.Failed("new-outlook", "Outlook did not return a calendar folder.");

            var mailbox = TryStr(() => Get(session!, "CurrentUser") is { } u ? Get(u, "Name") as string : null);

            var items = Get(folder, "Items");
            if (items is null) return ReadResult.Failed("error", "The calendar had no item collection.");

            // The standard idiom, and the order is load-bearing: Sort FIRST, then IncludeRecurrences, then
            // Restrict. Setting IncludeRecurrences before sorting silently breaks the subsequent Restrict -
            // it returns items from outside the requested window entirely (observed: asking for two months
            // returned hundreds of appointments from six months earlier). Outlook then hands back flattened
            // occurrences, so this never has to interpret a recurrence pattern itself.
            Invoke(items, "Sort", "[Start]", false);
            Set(items, "IncludeRecurrences", true);

            var restricted = Invoke(items, "Restrict", Filter(options.Start, options.End));
            if (restricted is null) return ReadResult.Failed("error", "The calendar filter returned nothing.");

            var (events, complete) = Enumerate(restricted, options);
            return new ReadResult
            {
                Ok = true,
                MailboxName = mailbox,
                DeviceTimeZone = TimeZoneInfo.Local.Id,
                Complete = complete,
                Events = events,
            };
        }
        catch (Exception ex)
        {
            return ReadResult.Failed(Classify(ex), Describe(ex));
        }
        finally
        {
            // Release, never Quit. Quitting would close the user's own running Outlook; letting the refcount
            // fall leaves an Outlook they had open exactly as it was.
            Release(folder);
            Release(session);
            Release(app);
        }
    }

    /// <summary>Attach to a running Outlook if there is one, else start it. Retried once on the two "busy"
    /// HRESULTs, which is what a modal dialog or a mid-send Outlook returns.</summary>
    private static object? CreateApplication()
    {
        var type = Type.GetTypeFromProgID("Outlook.Application");
        if (type is null) return null;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch (COMException ex) when (attempt == 0 && IsBusy(ex))
            {
                Thread.Sleep(2000);
            }
        }
    }

    private static (List<EventDto> Events, bool Complete) Enumerate(object items, ReaderOptions options)
    {
        var events = new List<EventDto>();
        var complete = true;

        object? current = null;
        try
        {
            current = Invoke(items, "GetFirst");
            while (current is not null && events.Count < options.Max)
            {
                try
                {
                    var dto = Project(current, options);
                    if (dto is not null) events.Add(dto);
                }
                catch
                {
                    // One unreadable appointment must not lose the whole window; skip it and carry on. The run
                    // still counts as complete - we saw the item, we just could not use it.
                }

                var next = Invoke(items, "GetNext");
                Release(current);
                current = next;
            }
        }
        catch (Exception)
        {
            // Enumeration died partway. Report what we have with complete = false: the server treats that as
            // "do not delete anything you were not told about", which is the whole point of the flag.
            complete = false;
        }
        finally
        {
            Release(current);
        }

        return (events, complete);
    }

    private static EventDto? Project(object item, ReaderOptions options)
    {
        // MeetingStatus 5 (olMeetingCanceled) / 7 (olMeetingReceivedAndCanceled): dropped rather than sent, so
        // the server's sweep removes any copy it already holds.
        var meetingStatus = Or(() => Convert.ToInt32(Get(item, "MeetingStatus") ?? 0), 0);
        if (meetingStatus is 5 or 7) return null;

        var sensitivity = Or(() => Convert.ToInt32(Get(item, "Sensitivity") ?? 0), 0);
        var isPrivate = sensitivity >= 2; // olPrivate / olConfidential
        if (options.SkipPrivate && isPrivate) return null;

        var start = TryVal(() => Convert.ToDateTime(Get(item, "Start"), CultureInfo.InvariantCulture));
        var end = TryVal(() => Convert.ToDateTime(Get(item, "End"), CultureInfo.InvariantCulture));
        if (start is null || end is null) return null;

        var allDay = Or(() => Convert.ToBoolean(Get(item, "AllDayEvent") ?? false), false);

        // A body is never sent for a private appointment, whatever the include-body setting says, and the HTML
        // body is never touched at all - it carries tracking pixels and inline images.
        var wantBody = options.IncludeBody && !isPrivate;
        var body = wantBody ? Truncate(TryStr(() => Get(item, "Body") as string), options.MaxBodyChars) : null;

        return new EventDto
        {
            Uid = Uid(item),
            Subject = TryStr(() => Get(item, "Subject") as string),
            // COM hands back local times; the server stores instants, so convert here rather than guessing there.
            Start = new DateTimeOffset(start.Value).ToUniversalTime(),
            End = new DateTimeOffset(end.Value).ToUniversalTime(),
            AllDay = allDay,
            // The local calendar dates, taken from the local DateTime's own parts. NEVER derived from the UTC
            // instant: an all-day 2026-03-15 in Europe/London is 2026-03-14T23:00:00Z, so going via UTC puts
            // every summer all-day entry a day early.
            StartDate = allDay ? start.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            EndDate = allDay ? end.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            WindowsTimeZoneId = TryStr(() => Get(Get(item, "StartTimeZone")!, "ID") as string),
            Location = TryStr(() => Get(item, "Location") as string),
            OnlineMeetingUrl = OnlineUrl(item),
            BodyText = body,
            Categories = TryStr(() => Get(item, "Categories") as string),
            Sensitivity = sensitivity,
            BusyStatus = Or(() => Convert.ToInt32(Get(item, "BusyStatus") ?? 2), 2),
            IsRecurring = Or(() => Convert.ToBoolean(Get(item, "IsRecurring") ?? false), false),
            OrganizerName = TryStr(() => Get(item, "Organizer") as string),
            OrganizerEmail = null, // Outlook exposes the organiser as a display name here; recipients carry the address
            Attendees = Attendees(item),
            LastModified = TryVal(() =>
                new DateTimeOffset(Convert.ToDateTime(Get(item, "LastModificationTime"), CultureInfo.InvariantCulture))
                    .ToUniversalTime()),
        };
    }

    /// <summary>The per-occurrence Global Object ID - the same value EWS returns as <c>calendar:UID</c>. Stable
    /// across moves between folders and stores, and distinct per instance of a series (Outlook stamps the
    /// occurrence date into it), which is why it beats EntryID (unstable) and CleanGlobalObjectId (one value for
    /// the whole series). Locally-created appointments sometimes have none, hence the EntryID fallback.</summary>
    private static string Uid(object item)
    {
        var global = TryStr(() => Get(item, "GlobalAppointmentID") as string);
        if (!string.IsNullOrWhiteSpace(global)) return global!;

        var entry = TryStr(() => Get(item, "EntryID") as string) ?? Guid.NewGuid().ToString("N");
        return "entry:" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(entry))).ToLowerInvariant();
    }

    private static List<AttendeeDto> Attendees(object item)
    {
        var list = new List<AttendeeDto>();
        object? recipients = null;
        try
        {
            recipients = Get(item, "Recipients");
            if (recipients is null) return list;

            var count = Convert.ToInt32(Get(recipients, "Count") ?? 0);
            for (var i = 1; i <= count; i++) // COM collections are 1-based
            {
                object? r = null;
                try
                {
                    r = Invoke(recipients, "Item", i);
                    if (r is null) continue;

                    list.Add(new AttendeeDto
                    {
                        Name = TryStr(() => Get(r, "Name") as string),
                        Email = TryStr(() => Get(r, "Address") as string),
                        Response = MapResponse(Or(() => Convert.ToInt32(Get(r, "MeetingResponseStatus") ?? 0), 0)),
                        // olOptional = 2
                        Optional = Or(() => Convert.ToInt32(Get(r, "Type") ?? 1), 1) == 2,
                    });
                }
                catch
                {
                    // Skip an unreadable recipient rather than losing the attendee list.
                }
                finally
                {
                    Release(r);
                }
            }
        }
        catch
        {
            // No recipients is normal for an appointment that is not a meeting.
        }
        finally
        {
            Release(recipients);
        }
        return list;
    }

    /// <summary>Outlook's numeric response status mapped to <b>Google's</b> vocabulary, so the shared
    /// CalendarAttendee projection and every existing renderer work without translation.</summary>
    private static string MapResponse(int status) => status switch
    {
        3 => "accepted",   // olResponseAccepted
        4 => "declined",   // olResponseDeclined
        2 => "tentative",  // olResponseTentative
        _ => "needsAction",
    };

    /// <summary>A Teams/Zoom join link if the appointment carries one. Newer Outlook exposes it directly;
    /// otherwise fall back to a URL in the location, which is where a pasted invite usually puts it.</summary>
    private static string? OnlineUrl(object item)
    {
        var direct = TryStr(() => Get(item, "OnlineMeetingUrl") as string);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        var location = TryStr(() => Get(item, "Location") as string);
        if (string.IsNullOrWhiteSpace(location)) return null;

        var at = location.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var url = location[at..].Split(' ', '\t', '\r', '\n')[0];
        return url.Length > 8 ? url : null;
    }

    /// <summary>Outlook's Restrict filter, in <b>DASL</b> form with ISO-8601 dates.
    ///
    /// <para>The obvious alternative - the bracketed <c>[Start] &lt; '...'</c> syntax - parses its dates using
    /// <b>Outlook's</b> locale, while this process formats them using its own. Under
    /// <c>InvariantGlobalization</c> that means writing <c>08/20/2026</c> (US order) and handing it to an
    /// en-GB Outlook that reads it as day 8 of month 20. The filter does not throw; it silently stops
    /// constraining anything, and Restrict hands back the whole folder - which is exactly what happened:
    /// asking for two months returned hundreds of appointments from six months earlier.</para>
    ///
    /// <para>DASL takes ISO-8601 and is locale-independent, so there is no format to get wrong. The comparison
    /// is an <b>overlap</b> test, so a meeting already under way when the window opens still belongs to it.</para></summary>
    private const string DtStart = "urn:schemas:calendar:dtstart";
    private const string DtEnd = "urn:schemas:calendar:dtend";

    private static string Filter(DateTime start, DateTime end) =>
        $"@SQL=\"{DtStart}\" < '{end.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}' AND " +
        $"\"{DtEnd}\" > '{start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}'";

    private static string Classify(Exception ex) => ex switch
    {
        COMException com when IsBusy(com) => "busy",
        COMException { HResult: unchecked((int)0x80070005) } => "denied",      // E_ACCESSDENIED
        COMException { HResult: unchecked((int)0x800401F3) } => "not-installed", // Invalid class string
        COMException { HResult: unchecked((int)0x80080005) } => "new-outlook",  // Server execution failed
        UnauthorizedAccessException => "denied",
        _ => "error",
    };

    private static bool IsBusy(COMException ex) =>
        ex.HResult is unchecked((int)0x80010001)   // RPC_E_CALL_REJECTED
                   or unchecked((int)0x8001010A);  // RPC_E_SERVERCALL_RETRYLATER

    private static string Describe(Exception ex) => ex.Message.Trim();

    // ---- late-bound COM helpers ----

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    // Three helpers rather than one generic: a single `T? TryGet<T>` collapses to `T` for value types, so
    // an int/bool/DateTime read would silently lose its "absent" case. A property Outlook refuses for a given
    // item is absent, not fatal - every read here is best-effort.
    private static string? TryStr(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private static T? TryVal<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch { return null; }
    }

    private static T Or<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static void Write(ReadResult result) =>
        Console.Out.Write(JsonSerializer.Serialize(result, JsonOpts));

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
