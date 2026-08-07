using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The desktop Outlook push endpoint and the connected-device CRUD.
///
/// <para>The reconciliation rules carry the risk here: the server deletes rows the latest run did not report,
/// so every guard against deleting on incomplete information is tested explicitly.</para></summary>
public class OutlookCalendarControllerTests
{
    private static OutlookCalendarController Build(DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    private static Guid SeedOptedInUser(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        db.UserSettings.Add(new UserSettings { UserId = userId, OutlookSyncEnabled = true });
        db.SaveChanges();
        return userId;
    }

    private static OutlookEventInput Event(
        string uid, string subject, string start, string end, DateTimeOffset? lastModified = null) =>
        new(uid, subject, DateTimeOffset.Parse(start), DateTimeOffset.Parse(end),
            LastModified: lastModified ?? DateTimeOffset.Parse("2026-07-01T00:00:00Z"));

    /// <summary>Age a device's last-sync stamp so the next run is past the 60s per-device cooldown. Real runs
    /// are minutes apart; back-to-back ones in a test would otherwise (correctly) be rejected. Mirrors how
    /// <c>WebhookDeliveryProcessorTests</c> seeds <c>LastAttemptAt</c> to steer its rolling-minute cap.</summary>
    private static async Task PastCooldown(DiarizDbContext db)
    {
        foreach (var s in db.OutlookCalendarSources) s.LastSyncedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
    }

    private static OutlookSyncRequest Push(
        Guid syncId, IReadOnlyList<OutlookEventInput> events, bool complete = true, bool final = true,
        string deviceId = "dev-1", string windowStart = "2026-07-01T00:00:00Z", string windowEnd = "2026-08-01T00:00:00Z") =>
        new(syncId, new OutlookDeviceDto(deviceId, "WORK-PC", "ken@x.test", "Europe/London"),
            DateTimeOffset.Parse(windowStart), DateTimeOffset.Parse(windowEnd), events, complete, 0, final);

    // ---- opt-in ----

    /// <summary>The privacy switch is a hard gate: nothing is stored until the user turns it on, so an
    /// installed desktop app on its own changes nothing.</summary>
    [Fact]
    public async Task Sync_WhenNotOptedIn_IsForbidden()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        await db.SaveChangesAsync();

        var result = await Build(db, userId).Sync(Push(Guid.NewGuid(), [Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")]), default);

        Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
        Assert.Empty(db.OutlookCalendarEvents);
        Assert.Empty(db.OutlookCalendarSources);
    }

    // ---- source provisioning ----

    [Fact]
    public async Task Sync_AutoProvisionsASourcePerDevice()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);

        await controller.Sync(Push(Guid.NewGuid(), [Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")], deviceId: "dev-1"), default);
        await PastCooldown(db);
        await controller.Sync(Push(Guid.NewGuid(), [Event("u2", "B", "2026-07-02T11:00:00Z", "2026-07-02T12:00:00Z")], deviceId: "dev-1"), default);
        await PastCooldown(db);
        await controller.Sync(Push(Guid.NewGuid(), [Event("u3", "C", "2026-07-02T13:00:00Z", "2026-07-02T14:00:00Z")], deviceId: "dev-2"), default);

        // Two machines, two sources - not three, and not one.
        Assert.Equal(2, db.OutlookCalendarSources.Count());
        Assert.Equal("WORK-PC", db.OutlookCalendarSources.First().DeviceName);
    }

    /// <summary>Two machines are independent mirrors. If they shared a source, each launch's sweep would delete
    /// the other machine's events.</summary>
    [Fact]
    public async Task Sync_DevicesDoNotSweepEachOther()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);

        await controller.Sync(Push(Guid.NewGuid(), [Event("laptop-only", "Laptop", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")], deviceId: "dev-1"), default);
        await controller.Sync(Push(Guid.NewGuid(), [Event("desktop-only", "Desktop", "2026-07-02T11:00:00Z", "2026-07-02T12:00:00Z")], deviceId: "dev-2"), default);

        Assert.Equal(2, db.OutlookCalendarEvents.Count());
    }

    // ---- upsert ----

    [Fact]
    public async Task Sync_IsIdempotent_AndCountsUnchangedRows()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);
        var events = new[] { Event("u1", "Planning", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z") };

        var first = await controller.Sync(Push(Guid.NewGuid(), events), default);
        await PastCooldown(db);
        var second = await controller.Sync(Push(Guid.NewGuid(), events), default);

        Assert.Equal(1, ((OutlookSyncResultDto)((OkObjectResult)first.Result!).Value!).Created);
        var repeat = (OutlookSyncResultDto)((OkObjectResult)second.Result!).Value!;
        Assert.Equal(0, repeat.Created);
        Assert.Equal(1, repeat.Unchanged);
        Assert.Equal(0, repeat.Deleted);
        Assert.Single(db.OutlookCalendarEvents);
    }

    /// <summary>Change detection is a fingerprint compare on Outlook's LastModificationTime - no content
    /// hashing - so an unchanged appointment costs nothing but a timestamp stamp.</summary>
    [Fact]
    public async Task Sync_UpdatesOnlyWhenTheFingerprintMoved()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);

        await controller.Sync(Push(Guid.NewGuid(), [Event("u1", "Planning", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")]), default);
        var edited = Event("u1", "Planning (moved)", "2026-07-02T14:00:00Z", "2026-07-02T15:00:00Z",
            lastModified: DateTimeOffset.Parse("2026-07-02T08:00:00Z"));
        await PastCooldown(db);
        var result = await controller.Sync(Push(Guid.NewGuid(), [edited]), default);

        Assert.Equal(1, ((OutlookSyncResultDto)((OkObjectResult)result.Result!).Value!).Updated);
        var row = db.OutlookCalendarEvents.Single();
        Assert.Equal("Planning (moved)", row.Subject);
        Assert.Equal(DateTimeOffset.Parse("2026-07-02T14:00:00Z"), row.StartsAt);
    }

    // ---- the sweep, and every guard on it ----

    [Fact]
    public async Task Sync_SweepsInWindowRowsTheRunDidNotReport()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);

        await controller.Sync(Push(Guid.NewGuid(), [
            Event("keep", "Keep", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
            Event("cancelled", "Cancelled", "2026-07-03T09:00:00Z", "2026-07-03T10:00:00Z"),
        ]), default);

        await PastCooldown(db);
        // The cancelled meeting is simply absent from the next run - the desktop drops cancellations.
        var result = await controller.Sync(Push(Guid.NewGuid(), [
            Event("keep", "Keep", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
        ]), default);

        Assert.Equal(1, ((OutlookSyncResultDto)((OkObjectResult)result.Result!).Value!).Deleted);
        Assert.Equal("keep", db.OutlookCalendarEvents.Single().Uid);
    }

    /// <summary>The direct fix for the reference implementation's worst bug: a COM read that threw halfway
    /// still pushes what it got, and must never be read as "everything else was deleted".</summary>
    [Fact]
    public async Task Sync_WhenIncomplete_NeverSweeps()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);
        await controller.Sync(Push(Guid.NewGuid(), [
            Event("a", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
            Event("b", "B", "2026-07-03T09:00:00Z", "2026-07-03T10:00:00Z"),
        ]), default);
        await PastCooldown(db);

        var result = await controller.Sync(Push(Guid.NewGuid(), [
            Event("a", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
        ], complete: false), default);

        Assert.Equal(0, ((OutlookSyncResultDto)((OkObjectResult)result.Result!).Value!).Deleted);
        Assert.Equal(2, db.OutlookCalendarEvents.Count());
    }

    /// <summary>A run that never reaches its last page degrades to upsert-only, so a dropped connection
    /// mid-window cannot delete the rest of the calendar.</summary>
    [Fact]
    public async Task Sync_NonFinalPage_NeverSweeps()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);
        await controller.Sync(Push(Guid.NewGuid(), [
            Event("a", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
            Event("b", "B", "2026-07-03T09:00:00Z", "2026-07-03T10:00:00Z"),
        ]), default);
        await PastCooldown(db);

        var result = await controller.Sync(Push(Guid.NewGuid(), [
            Event("a", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
        ], final: false), default);

        Assert.Equal(0, ((OutlookSyncResultDto)((OkObjectResult)result.Result!).Value!).Deleted);
        Assert.Equal(2, db.OutlookCalendarEvents.Count());
    }

    /// <summary>The sweep is bounded by the window the run actually covered, so narrowing the date range in
    /// Preferences never silently deletes history outside it.</summary>
    [Fact]
    public async Task Sync_SweepLeavesRowsOutsideTheWindowAlone()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);
        await controller.Sync(Push(Guid.NewGuid(), [
            Event("old", "Old", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
        ]), default);
        await PastCooldown(db);

        // A later, narrower run covering only August.
        var result = await controller.Sync(Push(Guid.NewGuid(), [
            Event("new", "New", "2026-08-05T09:00:00Z", "2026-08-05T10:00:00Z"),
        ], windowStart: "2026-08-01T00:00:00Z", windowEnd: "2026-09-01T00:00:00Z"), default);

        Assert.Equal(0, ((OutlookSyncResultDto)((OkObjectResult)result.Result!).Value!).Deleted);
        Assert.Equal(2, db.OutlookCalendarEvents.Count());
    }

    // ---- timezone ----

    /// <summary>Windows zone ids are converted server-side with .NET's built-in ICU mapping. The field must
    /// never be "declared but never populated" - the reference implementation's bug - so an unmappable id
    /// falls back to the device's own zone rather than staying null.</summary>
    [Theory]
    [InlineData("GMT Standard Time", "Europe/London")]
    [InlineData("Pacific Standard Time", "America/Los_Angeles")]
    public async Task Sync_ConvertsWindowsTimeZoneIdsToIana(string windows, string expected)
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var ev = Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z") with { WindowsTimeZoneId = windows };

        await Build(db, userId).Sync(Push(Guid.NewGuid(), [ev]), default);

        var row = db.OutlookCalendarEvents.Single();
        Assert.Equal(expected, row.TimeZoneId);
        Assert.Equal(windows, row.WindowsTimeZoneId);   // raw value kept for diagnosis
    }

    [Fact]
    public async Task Sync_WhenTheWindowsZoneIsUnknown_FallsBackToTheDeviceZone()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var ev = Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z") with { WindowsTimeZoneId = "Nonexistent Standard Time" };

        await Build(db, userId).Sync(Push(Guid.NewGuid(), [ev]), default);

        Assert.Equal("Europe/London", db.OutlookCalendarEvents.Single().TimeZoneId);
    }

    /// <summary>All-day dates are stored as the local date strings the desktop sent, never re-derived from the
    /// UTC instant - re-deriving is the off-by-one that puts every summer all-day entry a day early.</summary>
    [Fact]
    public async Task Sync_PreservesAllDayDatesVerbatim()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var ev = Event("holiday", "Bank holiday", "2026-07-01T23:00:00Z", "2026-07-02T23:00:00Z") with
        {
            AllDay = true, StartDate = "2026-07-02", EndDate = "2026-07-03",
        };

        await Build(db, userId).Sync(Push(Guid.NewGuid(), [ev]), default);

        var row = db.OutlookCalendarEvents.Single();
        Assert.True(row.AllDay);
        Assert.Equal("2026-07-02", row.StartDate);
        Assert.Equal("2026-07-03", row.EndDate);
    }

    // ---- limits ----

    /// <summary>A second full run straight after the first is rate-limited per device, so a stuck client (or an
    /// impatient hand on "Sync now") cannot rewrite the whole window repeatedly.</summary>
    [Fact]
    public async Task Sync_RejectsASecondRunInsideTheCooldown()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);
        var events = new[] { Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z") };
        await controller.Sync(Push(Guid.NewGuid(), events), default);

        var result = await controller.Sync(Push(Guid.NewGuid(), events), default);

        Assert.Equal(409, ((ObjectResult)result.Result!).StatusCode);
    }

    /// <summary>The cooldown gates whole runs, not pages: a multi-page run already in flight must be able to
    /// finish, or the window would be left half-written with no final page to sweep or stamp it.</summary>
    [Fact]
    public async Task Sync_LaterPagesOfTheSameRunAreNotRateLimited()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var controller = Build(db, userId);
        var runId = Guid.NewGuid();

        var first = await controller.Sync(
            Push(runId, [Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")], final: false), default);
        var second = await controller.Sync(
            Push(runId, [Event("u2", "B", "2026-07-02T11:00:00Z", "2026-07-02T12:00:00Z")], final: true) with { PageIndex = 1 },
            default);

        Assert.IsType<OkObjectResult>(first.Result);
        Assert.IsType<OkObjectResult>(second.Result);
        Assert.Equal(2, db.OutlookCalendarEvents.Count());
    }

    [Fact]
    public async Task Sync_RejectsAnOversizedPage()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var many = Enumerable.Range(0, 501)
            .Select(i => Event($"u{i}", $"E{i}", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"))
            .ToList();

        var result = await Build(db, userId).Sync(Push(Guid.NewGuid(), many), default);

        Assert.Equal(413, ((ObjectResult)result.Result!).StatusCode);
        Assert.Empty(db.OutlookCalendarEvents);
    }

    [Fact]
    public async Task Sync_RejectsAnAbsurdlyLongUid()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var ev = Event(new string('a', 401), "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z");

        var result = await Build(db, userId).Sync(Push(Guid.NewGuid(), [ev]), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Sync_RejectsAnAbsurdlyWideWindow()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);

        var result = await Build(db, userId).Sync(Push(
            Guid.NewGuid(), [Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")],
            windowStart: "2020-01-01T00:00:00Z", windowEnd: "2026-01-01T00:00:00Z"), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Sync_TruncatesAnOverlongBody()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        var ev = Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z") with { BodyText = new string('x', 12_000) };

        await Build(db, userId).Sync(Push(Guid.NewGuid(), [ev]), default);

        Assert.Equal(8000, db.OutlookCalendarEvents.Single().BodyText!.Length);
    }

    // ---- sources CRUD ----

    [Fact]
    public async Task Sources_ListsOnlyTheCallersDevices()
    {
        using var db = TestDb.Create();
        var mine = SeedOptedInUser(db);
        var theirs = SeedOptedInUser(db);
        await Build(db, mine).Sync(Push(Guid.NewGuid(), [Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")]), default);
        await Build(db, theirs).Sync(Push(Guid.NewGuid(), [Event("u2", "B", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")], deviceId: "dev-x"), default);

        var listed = Assert.IsType<OkObjectResult>((await Build(db, mine).Sources(default)).Result);
        var sources = Assert.IsAssignableFrom<IReadOnlyList<OutlookSourceDto>>(listed.Value);
        Assert.Single(sources);
        Assert.Equal(1, sources[0].EventCount);
    }

    [Fact]
    public async Task UpdateSource_AppliesTheFieldsSent_AndClampsTheWindow()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        await Build(db, userId).Sync(Push(Guid.NewGuid(), [Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")]), default);
        var id = db.OutlookCalendarSources.Single().Id;

        var result = await Build(db, userId).UpdateSource(
            id, new OutlookSourceRequest(DisplayName: "Work laptop", Enabled: false, PastDays: 9999, FutureDays: 0), default);

        var dto = (OutlookSourceDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("Work laptop", dto.DisplayName);
        Assert.False(dto.Enabled);
        Assert.Equal(365, dto.PastDays);      // clamped
        Assert.Equal(1, dto.FutureDays);      // clamped
        Assert.True(dto.SkipPrivate);         // untouched by an omitted field
    }

    /// <summary>Disconnecting a device removes its stored copy too - a privacy control has to actually
    /// erase.</summary>
    [Fact]
    public async Task DeleteSource_RemovesItsStoredEvents()
    {
        using var db = TestDb.Create();
        var userId = SeedOptedInUser(db);
        await Build(db, userId).Sync(Push(Guid.NewGuid(), [
            Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z"),
            Event("u2", "B", "2026-07-03T09:00:00Z", "2026-07-03T10:00:00Z"),
        ]), default);
        var id = db.OutlookCalendarSources.Single().Id;

        Assert.IsType<NoContentResult>(await Build(db, userId).DeleteSource(id, default));
        Assert.Empty(db.OutlookCalendarSources);
        Assert.Empty(db.OutlookCalendarEvents);
    }

    /// <summary>Another user's source is 404, never 403 - existence is not leaked.</summary>
    [Fact]
    public async Task Sources_AreOwnershipScoped()
    {
        using var db = TestDb.Create();
        var owner = SeedOptedInUser(db);
        var stranger = SeedOptedInUser(db);
        await Build(db, owner).Sync(Push(Guid.NewGuid(), [Event("u1", "A", "2026-07-02T09:00:00Z", "2026-07-02T10:00:00Z")]), default);
        var id = db.OutlookCalendarSources.Single(s => s.UserId == owner).Id;

        Assert.IsType<NotFoundResult>((await Build(db, stranger).UpdateSource(id, new OutlookSourceRequest(DisplayName: "x"), default)).Result);
        Assert.IsType<NotFoundResult>(await Build(db, stranger).DeleteSource(id, default));
        Assert.Single(db.OutlookCalendarSources.Where(s => s.UserId == owner));
    }
}
