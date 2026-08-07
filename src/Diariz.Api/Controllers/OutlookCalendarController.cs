using System.Security.Claims;
using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The desktop Outlook connector: the push endpoint the Windows app sends its calendar window to, and
/// the CRUD for the devices a user has connected.
///
/// <para>Google Calendar and <c>.ics</c> feeds are fetched live and never stored. Outlook is different - it
/// lives on the user's PC and only the desktop app can read it - so its events are pushed here and persisted,
/// which is what lets them keep working in a browser and after the app is closed.</para>
///
/// <para>Authenticated with the <b>user's own JWT</b>, sent by the web app running inside the desktop shell -
/// deliberately not the <c>internal/</c> + <c>X-Worker-Secret</c> pattern, which is a server-to-server
/// credential that would leak to every installer if it were embedded in a desktop binary.</para></summary>
[ApiController]
[Authorize]
[Route("api/calendar/outlook")]
public class OutlookCalendarController : ControllerBase
{
    /// <summary>Per page. Bigger than the desktop's own 250 so a slightly generous client still succeeds.</summary>
    private const int MaxEventsPerPage = 500;
    private const int MaxUidLength = 400;
    private const int MaxBodyLength = 8000;
    private const int MaxWindowDays = 730;
    /// <summary>Minimum gap between accepted <b>runs</b> for one device. Further pages of a run already in
    /// flight share its sync id and are exempt.</summary>
    private static readonly TimeSpan RunCooldown = TimeSpan.FromSeconds(60);

    private readonly DiarizDbContext _db;

    public OutlookCalendarController(DiarizDbContext db) => _db = db;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("sync")]
    [EndpointSummary("Push a window of desktop Outlook calendar events")]
    [EndpointDescription(
        "Mirrors a window of your **classic desktop Outlook** calendar into Diariz, so those meetings behave " +
        "like your Google or `.ics` ones - they show on the Calendar tab, can be matched to a recording, and " +
        "carry pre-meeting notes - and keep working when the desktop app is closed.\n\n" +
        "Send the **whole window** and let the server reconcile: it inserts what is new, updates what changed " +
        "(compared on Outlook's own last-modified time), and deletes what you no longer report. Pushing the " +
        "same window twice is a no-op, so a retry is always safe.\n\n" +
        "Deletion is deliberately hard to trigger by accident. It only happens on the page marked `final`, " +
        "only when `complete` is true, and only for events inside `windowStart`..`windowEnd`. If your read " +
        "failed partway, send `complete: false` and nothing is deleted. Cancelled appointments simply stop " +
        "being reported.\n\n" +
        "The device id makes this per-machine: two PCs are two independent mirrors that never delete each " +
        "other's events. Requires the Outlook sync opt-in in Preferences (403 otherwise).")]
    public async Task<ActionResult<OutlookSyncResultDto>> Sync(OutlookSyncRequest req, CancellationToken ct)
    {
        // The privacy opt-in gates everything: storing meeting bodies and attendee addresses server-side is
        // not something an installed desktop app should be able to start on its own.
        var settings = await _db.UserSettings.FirstOrDefaultAsync(s => s.UserId == UserId, ct);
        if (settings?.OutlookSyncEnabled != true)
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "outlook-sync-disabled" });

        if (string.IsNullOrWhiteSpace(req.Device?.DeviceId) || req.Device.DeviceId.Length > 64)
            return BadRequest("A device id is required.");
        if (req.WindowEnd <= req.WindowStart)
            return BadRequest("windowEnd must be after windowStart.");
        if (req.WindowEnd - req.WindowStart > TimeSpan.FromDays(MaxWindowDays))
            return BadRequest($"The window must be no wider than {MaxWindowDays} days.");

        var events = req.Events ?? [];
        if (events.Count > MaxEventsPerPage)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, $"At most {MaxEventsPerPage} events per page.");
        if (events.Any(e => string.IsNullOrWhiteSpace(e.Uid) || e.Uid.Length > MaxUidLength))
            return BadRequest($"Every event needs a uid of at most {MaxUidLength} characters.");

        var now = DateTimeOffset.UtcNow;
        var source = await _db.OutlookCalendarSources
            .FirstOrDefaultAsync(s => s.UserId == UserId && s.DeviceId == req.Device.DeviceId, ct);

        if (source is null)
        {
            // Auto-provision: there is no "add a device" step in the UI - connecting is just syncing.
            source = new OutlookCalendarSource
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                DeviceId = req.Device.DeviceId,
                DeviceName = Clamp(req.Device.DeviceName, 128),
                MailboxName = Clamp(req.Device.MailboxName, 256),
                DisplayName = Clamp(
                    string.IsNullOrWhiteSpace(req.Device.DeviceName) ? "Outlook" : $"Outlook ({req.Device.DeviceName})",
                    128)!,
            };
            _db.OutlookCalendarSources.Add(source);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            // A rename or a mailbox switch on the machine should show through.
            source.DeviceName = Clamp(req.Device.DeviceName, 128) ?? source.DeviceName;
            source.MailboxName = Clamp(req.Device.MailboxName, 256) ?? source.MailboxName;

            // Rate limit whole runs, not pages: a run already in flight keeps the same sync id, and its later
            // pages must not be rejected halfway through (which would strand the window half-written).
            var isNewRun = req.PageIndex == 0;
            var lastRunId = await _db.OutlookCalendarEvents
                .Where(e => e.SourceId == source.Id)
                .OrderByDescending(e => e.SyncedAt)
                .Select(e => (Guid?)e.SyncId)
                .FirstOrDefaultAsync(ct);
            if (isNewRun && lastRunId != req.SyncId &&
                source.LastSyncedAt is { } last && now - last < RunCooldown)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    code = "outlook-sync-cooldown",
                    retryAfterSeconds = (int)Math.Ceiling((RunCooldown - (now - last)).TotalSeconds),
                });
            }
        }

        var deviceZone = req.Device.TimeZone;
        var created = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var input in events)
        {
            var id = OutlookEventId.For(source.Id, input.Uid);
            var row = await _db.OutlookCalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct);
            var fingerprint = input.LastModified ?? DateTimeOffset.MinValue;

            if (row is null)
            {
                row = new OutlookCalendarEvent { Id = id, SourceId = source.Id, UserId = UserId, Uid = input.Uid };
                _db.OutlookCalendarEvents.Add(row);
                Apply(row, input, deviceZone, source);
                created++;
            }
            else if (row.SourceLastModified != fingerprint)
            {
                Apply(row, input, deviceZone, source);
                updated++;
            }
            else
            {
                // Untouched since we last saw it - stamp it so the sweep knows this run reported it, and move on.
                unchanged++;
            }

            row.SyncId = req.SyncId;
            row.SyncedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        var deleted = 0;
        if (req is { Final: true, Complete: true })
        {
            // Scoping is structural rather than marker-based: this table holds only rows this sync created for
            // this source, so unlike a mirror written into someone's real calendar there is nothing else the
            // sweep could possibly hit. Bounded by the window the run actually covered, so a narrower run never
            // deletes history outside it.
            //
            // Loaded and removed rather than ExecuteDelete: the set is small (the meetings cancelled or moved
            // since the last run), and this keeps the safety rules above exercisable by the unit tests, which
            // is worth more here than saving one round trip on the riskiest operation in the feature.
            var stale = await _db.OutlookCalendarEvents
                .Where(e => e.SourceId == source.Id
                            && e.StartsAt >= req.WindowStart && e.StartsAt < req.WindowEnd
                            && e.SyncId != req.SyncId)
                .ToListAsync(ct);
            _db.OutlookCalendarEvents.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
            deleted = stale.Count;
        }

        if (req.Final)
        {
            source.LastSyncedAt = now;
            source.LastError = null;
            source.LastEventCount = await _db.OutlookCalendarEvents.CountAsync(e => e.SourceId == source.Id, ct);
        }
        await _db.SaveChangesAsync(ct);

        var total = await _db.OutlookCalendarEvents.CountAsync(e => e.SourceId == source.Id, ct);
        return Ok(new OutlookSyncResultDto(
            source.Id, req.SyncId, created, updated, unchanged, deleted, Skipped: 0, total, now));
    }

    [HttpGet("sources")]
    [EndpointSummary("List your connected Outlook devices")]
    [EndpointDescription(
        "Every machine that has mirrored its desktop Outlook calendar here, with how many events it currently " +
        "holds, when it last synced, and the last failure if there was one - so a connector broken on one PC " +
        "is visible from any other, or from a plain browser.\n\n" +
        "Empty when nothing is connected; that is the normal answer, not an error.")]
    public async Task<ActionResult<IReadOnlyList<OutlookSourceDto>>> Sources(CancellationToken ct)
    {
        var rows = await _db.OutlookCalendarSources
            .Where(s => s.UserId == UserId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                Source = s,
                Count = _db.OutlookCalendarEvents.Count(e => e.SourceId == s.Id),
            })
            .ToListAsync(ct);

        return Ok(rows.Select(r => ToDto(r.Source, r.Count)).ToList());
    }

    [HttpPut("sources/{id:guid}")]
    [EndpointSummary("Update a connected Outlook device")]
    [EndpointDescription(
        "Rename a device, recolour its events, hide it without disconnecting, change how far back and forward " +
        "it reads, or change what it is allowed to send (private appointments, meeting bodies).\n\n" +
        "Every field is optional - omit one to leave it as it is. The date window is clamped to sane bounds " +
        "rather than rejected. Turning `enabled` off keeps the stored events but excludes them everywhere; to " +
        "erase them, delete the device.")]
    public async Task<ActionResult<OutlookSourceDto>> UpdateSource(Guid id, OutlookSourceRequest req, CancellationToken ct)
    {
        var source = await _db.OutlookCalendarSources.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (source is null) return NotFound();

        if (req.DisplayName is { } name && !string.IsNullOrWhiteSpace(name)) source.DisplayName = Clamp(name, 128)!;
        if (req.Color is { } color) source.Color = string.IsNullOrWhiteSpace(color) ? null : Clamp(color, 32);
        if (req.Enabled is { } enabled) source.Enabled = enabled;
        if (req.PastDays is { } past) source.PastDays = Math.Clamp(past, 0, 365);
        if (req.FutureDays is { } future) source.FutureDays = Math.Clamp(future, 1, 730);
        if (req.SkipPrivate is { } skip) source.SkipPrivate = skip;
        if (req.IncludeBody is { } body) source.IncludeBody = body;
        // Empty string clears a stale failure; null leaves whatever is recorded.
        if (req.LastError is { } error) source.LastError = string.IsNullOrWhiteSpace(error) ? null : Clamp(error, 512);

        await _db.SaveChangesAsync(ct);
        var count = await _db.OutlookCalendarEvents.CountAsync(e => e.SourceId == source.Id, ct);
        return Ok(ToDto(source, count));
    }

    [HttpDelete("sources/{id:guid}")]
    [EndpointSummary("Disconnect an Outlook device")]
    [EndpointDescription(
        "Disconnects the machine and **deletes every event mirrored from it**. This is the erase control, not " +
        "a pause - to keep the copy but hide it, set `enabled` to false instead.\n\n" +
        "Recordings already linked to one of these meetings keep their own stored snapshot, exactly as they do " +
        "when a Google event is deleted.")]
    public async Task<IActionResult> DeleteSource(Guid id, CancellationToken ct)
    {
        var source = await _db.OutlookCalendarSources.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (source is null) return NotFound();

        _db.OutlookCalendarSources.Remove(source);   // events cascade
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static OutlookSourceDto ToDto(OutlookCalendarSource s, int eventCount) => new(
        s.Id, s.DeviceId, s.DeviceName, s.MailboxName, s.DisplayName, s.Color, s.Enabled,
        s.PastDays, s.FutureDays, s.SkipPrivate, s.IncludeBody, s.LastSyncedAt, s.LastError, eventCount);

    /// <summary>Copy one reported occurrence onto its row.</summary>
    private static void Apply(
        OutlookCalendarEvent row, OutlookEventInput input, string? deviceZone, OutlookCalendarSource source)
    {
        row.Subject = Clamp(input.Subject, 512);
        row.StartsAt = input.Start.ToUniversalTime();
        row.EndsAt = input.End.ToUniversalTime();
        row.AllDay = input.AllDay;
        // Stored verbatim, never re-derived from the UTC instant: an all-day entry on 2026-03-15 in
        // Europe/London is the instant 2026-03-14T23:00:00Z, so re-deriving puts every summer all-day entry a
        // day early. The local date string the desktop computed is the display truth.
        row.StartDate = input.AllDay ? Clamp(input.StartDate, 10) : null;
        row.EndDate = input.AllDay ? Clamp(input.EndDate, 10) : null;
        row.WindowsTimeZoneId = Clamp(input.WindowsTimeZoneId, 128);
        row.TimeZoneId = ToIana(input.WindowsTimeZoneId, deviceZone);
        row.Location = Clamp(input.Location, 1024);
        row.OnlineMeetingUrl = Clamp(input.OnlineMeetingUrl, 2048);
        row.Categories = Clamp(input.Categories, 512);
        row.Sensitivity = input.Sensitivity;
        row.BusyStatus = input.BusyStatus;
        row.IsRecurring = input.IsRecurring;
        row.OrganizerName = Clamp(input.OrganizerName, 256);
        row.OrganizerEmail = Clamp(input.OrganizerEmail, 256);
        row.SourceLastModified = (input.LastModified ?? DateTimeOffset.MinValue).ToUniversalTime();

        // Defence in depth: the desktop already strips the body of a private appointment and honours
        // IncludeBody, but a body must never survive either rule server-side either.
        var privateItem = input.Sensitivity >= 2; // olPrivate / olConfidential
        row.BodyText = !source.IncludeBody || privateItem ? null : Clamp(input.BodyText, MaxBodyLength);

        row.AttendeesJson = input.Attendees is { Count: > 0 }
            ? JsonSerializer.Serialize(input.Attendees.Select(a =>
                new OutlookAttendeeJson(Clamp(a.Name, 256), Clamp(a.Email, 256), a.Response, a.Optional)))
            : null;
    }

    /// <summary>Windows zone id to IANA, using .NET's built-in ICU mapping so there is no CLDR table to ship.
    /// Falls back to the device's own zone rather than leaving the field null - a "declared but never
    /// populated" timezone is exactly the trap the reference implementation fell into.</summary>
    private static string? ToIana(string? windowsId, string? deviceZone)
    {
        if (!string.IsNullOrWhiteSpace(windowsId) &&
            TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var iana))
        {
            return iana;
        }
        return deviceZone;
    }

    private static string? Clamp(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
