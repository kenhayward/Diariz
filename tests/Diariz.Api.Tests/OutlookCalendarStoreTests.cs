using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Reading the stored Outlook mirror back out as the shared <see cref="CalendarEvent"/> projection -
/// the same shape Google and <c>.ics</c> produce, so nothing downstream has to know where an event came
/// from.</summary>
public class OutlookCalendarStoreTests
{
    private static async Task<(OutlookCalendarSource Source, Guid UserId)> SeedSource(
        DiarizDbContext db, bool enabled = true, string color = "#0F6CBD")
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var source = new OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = userId, DeviceId = "dev-1", DeviceName = "WORK-PC",
            DisplayName = "Outlook (WORK-PC)", Color = color, Enabled = enabled,
        };
        db.OutlookCalendarSources.Add(source);
        await db.SaveChangesAsync();
        return (source, userId);
    }

    private static OutlookCalendarEvent Timed(
        OutlookCalendarSource src, string uid, DateTimeOffset start, DateTimeOffset end, string? subject = "Planning") =>
        new()
        {
            Id = OutlookEventId.For(src.Id, uid), SourceId = src.Id, UserId = src.UserId, Uid = uid,
            Subject = subject, StartsAt = start, EndsAt = end,
        };

    [Fact]
    public async Task ListEvents_ProjectsIntoTheSharedCalendarEventShape()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        var ev = Timed(src, "uid-1",
            DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z"));
        ev.Location = "Room 3";
        ev.BodyText = "Agenda: the thing";
        ev.OnlineMeetingUrl = "https://teams.example/join/abc";
        ev.OrganizerName = "Ada";
        ev.OrganizerEmail = "ada@example.test";
        ev.AttendeesJson = """[{"name":"Bob","email":"bob@example.test","response":"accepted","optional":false}]""";
        db.OutlookCalendarEvents.Add(ev);
        await db.SaveChangesAsync();

        var store = new OutlookCalendarStore(db);
        var result = await store.ListEventsAsync(
            userId, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-07-03T00:00:00Z"));

        var got = Assert.Single(result);
        Assert.Equal(OutlookEventId.EventKey(ev.Id), got.Id);
        Assert.Equal(OutlookEventId.CalendarKey(src.Id), got.CalendarId);
        Assert.Equal("Outlook (WORK-PC)", got.CalendarName);
        Assert.Equal("#0F6CBD", got.Color);
        Assert.Equal("Planning", got.Summary);
        Assert.Equal("Room 3", got.Location);
        Assert.Equal("Agenda: the thing", got.Description);
        // A local Outlook item has no web permalink, so the join link is the only clickable target.
        Assert.Equal("https://teams.example/join/abc", got.HtmlLink);
        Assert.Equal("ada@example.test", got.Organizer!.Email);
        Assert.True(got.Organizer.Organizer);
        var attendee = Assert.Single(got.Attendees!);
        Assert.Equal("bob@example.test", attendee.Email);
        Assert.Equal("accepted", attendee.ResponseStatus);
        Assert.False(got.AllDay);
    }

    /// <summary>All-day entries must project exactly as Google's and <c>.ics</c>'s do, so the web app's
    /// day-grouping is one shared code path across all three sources rather than three subtly different ones.
    ///
    /// <para>That shared shape is <b>midnight UTC</b>. A date-only value names a calendar date and carries no
    /// offset, so the one chosen to put it on the wire has to be fixed rather than inherited: a plain
    /// <c>DateTimeOffset.Parse</c> stamps <c>TimeZoneInfo.Local</c>, which makes the API's own deployment
    /// timezone part of its response. <c>.ics</c> already emits <c>TimeSpan.Zero</c>; this matches it.</para>
    ///
    /// <para>The assertion only bites on a host that is not already UTC - which is exactly the case the fix is
    /// for, and why the fault survived a UTC-container CI run.</para></summary>
    [Fact]
    public async Task ListEvents_ProjectsAllDayAtMidnightUtc_WhateverTheHostTimezone()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        var ev = Timed(src, "uid-holiday",
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"), DateTimeOffset.Parse("2026-07-03T00:00:00Z"), "Bank holiday");
        ev.AllDay = true;
        ev.StartDate = "2026-07-02";
        ev.EndDate = "2026-07-03";     // exclusive next midnight, as Google and iCalendar both use
        db.OutlookCalendarEvents.Add(ev);
        await db.SaveChangesAsync();

        var store = new OutlookCalendarStore(db);
        var got = Assert.Single(await store.ListEventsAsync(
            userId, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-07-04T00:00:00Z")));

        Assert.True(got.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero), got.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero), got.End);
        // Spelled out separately: the instants above could match while the offset carried the host's zone.
        Assert.Equal(TimeSpan.Zero, got.Start.Offset);
        Assert.Equal(TimeSpan.Zero, got.End.Offset);
    }

    /// <summary>The window is an overlap test, not a containment test - a meeting already under way when the
    /// window opens still belongs to it.</summary>
    [Fact]
    public async Task ListEvents_MatchesOnOverlap_NotContainment()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        db.OutlookCalendarEvents.AddRange(
            Timed(src, "before", DateTimeOffset.Parse("2026-07-01T08:00:00Z"), DateTimeOffset.Parse("2026-07-01T09:00:00Z"), "Before"),
            Timed(src, "straddles", DateTimeOffset.Parse("2026-07-01T23:30:00Z"), DateTimeOffset.Parse("2026-07-02T00:30:00Z"), "Straddles"),
            Timed(src, "inside", DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z"), "Inside"),
            Timed(src, "after", DateTimeOffset.Parse("2026-07-04T09:00:00Z"), DateTimeOffset.Parse("2026-07-04T10:00:00Z"), "After"));
        await db.SaveChangesAsync();

        var store = new OutlookCalendarStore(db);
        var got = await store.ListEventsAsync(
            userId, DateTimeOffset.Parse("2026-07-02T00:00:00Z"), DateTimeOffset.Parse("2026-07-03T00:00:00Z"));

        Assert.Equal(["Straddles", "Inside"], got.Select(e => e.Summary));
    }

    [Fact]
    public async Task ListEvents_OrdersByStart()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        db.OutlookCalendarEvents.AddRange(
            Timed(src, "late", DateTimeOffset.Parse("2026-07-02T15:00:00Z"), DateTimeOffset.Parse("2026-07-02T16:00:00Z"), "Late"),
            Timed(src, "early", DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z"), "Early"));
        await db.SaveChangesAsync();

        var store = new OutlookCalendarStore(db);
        var got = await store.ListEventsAsync(
            userId, DateTimeOffset.Parse("2026-07-02T00:00:00Z"), DateTimeOffset.Parse("2026-07-03T00:00:00Z"));

        Assert.Equal(["Early", "Late"], got.Select(e => e.Summary));
    }

    /// <summary>A disabled device is kept but excluded from reads, exactly like a disabled <c>.ics</c> feed.</summary>
    [Fact]
    public async Task ListEvents_ExcludesDisabledSources()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db, enabled: false);
        db.OutlookCalendarEvents.Add(Timed(src, "uid-1",
            DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z")));
        await db.SaveChangesAsync();

        var store = new OutlookCalendarStore(db);
        Assert.Empty(await store.ListEventsAsync(
            userId, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-07-03T00:00:00Z")));
    }

    [Fact]
    public async Task ListEvents_NeverReturnsAnotherUsersEvents()
    {
        using var db = TestDb.Create();
        var (src, _) = await SeedSource(db);
        db.OutlookCalendarEvents.Add(Timed(src, "uid-1",
            DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z")));
        await db.SaveChangesAsync();

        var store = new OutlookCalendarStore(db);
        Assert.Empty(await store.ListEventsAsync(
            Guid.NewGuid(), DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-07-03T00:00:00Z")));
    }

    [Fact]
    public async Task GetEvent_RoundTripsByPublicId_AndIgnoresForeignIds()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        var ev = Timed(src, "uid-1",
            DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z"));
        db.OutlookCalendarEvents.Add(ev);
        await db.SaveChangesAsync();

        var store = new OutlookCalendarStore(db);
        Assert.Equal("Planning", (await store.GetEventAsync(userId, OutlookEventId.EventKey(ev.Id)))!.Summary);
        Assert.Null(await store.GetEventAsync(userId, "ics:abc:def"));                        // another source
        Assert.Null(await store.GetEventAsync(userId, OutlookEventId.EventKey(Guid.NewGuid())));
        Assert.Null(await store.GetEventAsync(Guid.NewGuid(), OutlookEventId.EventKey(ev.Id))); // another user
    }

    [Fact]
    public async Task HasEnabledSource_ReflectsTheEnabledFlag()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        var store = new OutlookCalendarStore(db);
        Assert.True(await store.HasEnabledSourceAsync(userId));
        Assert.False(await store.HasEnabledSourceAsync(Guid.NewGuid()));

        src.Enabled = false;
        await db.SaveChangesAsync();
        Assert.False(await store.HasEnabledSourceAsync(userId));
    }

    // ---- Recurrence ----

    /// <summary>Outlook returns one GlobalAppointmentID for a whole series, so the desktop qualifies a
    /// recurring occurrence's uid as `{series}#{start}` (outlookSync.js). The part before the '#' is the
    /// series key - which is why Outlook needs no new field to support series history.</summary>
    [Fact]
    public async Task GetEvent_ReportsTheSeriesForARecurringOccurrence()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        var ev = Timed(src, "040000008200E00074C5B7101A82E008#2026-08-10T09:00:00Z",
            DateTimeOffset.Parse("2026-08-10T09:00:00Z"), DateTimeOffset.Parse("2026-08-10T09:30:00Z"));
        ev.IsRecurring = true;
        db.OutlookCalendarEvents.Add(ev);
        await db.SaveChangesAsync();
        var rowId = ev.Id;

        var store = new OutlookCalendarStore(db);
        var got = await store.GetEventAsync(userId, OutlookEventId.EventKey(rowId));

        Assert.NotNull(got);
        Assert.True(got!.Recurring);
        Assert.Equal("040000008200E00074C5B7101A82E008", got.SeriesId);
    }

    /// <summary>dedupeUids appends the same '#{start}' suffix to a NON-recurring event when two share a uid,
    /// so the separator alone must never be read as "this recurs" - or two unrelated one-offs that happened
    /// to collide would be presented to the user as the same recurring meeting.</summary>
    [Fact]
    public async Task GetEvent_DoesNotSplitANonRecurringUidThatContainsAHash()
    {
        using var db = TestDb.Create();
        var (src, userId) = await SeedSource(db);
        var ev = Timed(src, "collided-uid#2026-08-10T09:00:00Z",
            DateTimeOffset.Parse("2026-08-10T09:00:00Z"), DateTimeOffset.Parse("2026-08-10T09:30:00Z"));
        ev.IsRecurring = false;
        db.OutlookCalendarEvents.Add(ev);
        await db.SaveChangesAsync();
        var rowId = ev.Id;

        var store = new OutlookCalendarStore(db);
        var got = await store.GetEventAsync(userId, OutlookEventId.EventKey(rowId));

        Assert.NotNull(got);
        Assert.False(got!.Recurring);
        Assert.Null(got.SeriesId);
    }
}
