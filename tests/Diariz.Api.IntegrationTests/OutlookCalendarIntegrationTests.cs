using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres coverage for the mirrored desktop Outlook calendar - the parts the in-memory
/// provider cannot model: the <c>jsonb</c> attendee column, the unique <c>(SourceId, Uid)</c> index, and both
/// cascade paths (an event hangs off its source <i>and</i> its user, so a badly configured pair would either
/// block a user delete or orphan rows).</summary>
[Collection(IntegrationCollection.Name)]
public class OutlookCalendarIntegrationTests(ContainersFixture fx)
{
    private async Task<(Guid UserId, Guid SourceId)> SeedSource(string deviceId = "dev-1")
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var source = new OutlookCalendarSource
        {
            Id = Guid.NewGuid(), UserId = user.Id, DeviceId = deviceId, DeviceName = "WORK-PC",
            DisplayName = "Outlook (WORK-PC)",
        };
        db.Users.Add(user);
        db.OutlookCalendarSources.Add(source);
        await db.SaveChangesAsync();
        return (user.Id, source.Id);
    }

    private static OutlookCalendarEvent Event(Guid sourceId, Guid userId, string uid) => new()
    {
        Id = OutlookEventId.For(sourceId, uid),
        SourceId = sourceId,
        UserId = userId,
        Uid = uid,
        Subject = "Planning",
        StartsAt = DateTimeOffset.Parse("2026-07-02T09:00:00Z"),
        EndsAt = DateTimeOffset.Parse("2026-07-02T10:00:00Z"),
        SyncId = Guid.NewGuid(),
    };

    [Fact]
    public async Task AttendeesJson_RoundTripsThroughTheJsonbColumn()
    {
        var (userId, sourceId) = await SeedSource();
        var ev = Event(sourceId, userId, "uid-attendees");
        ev.AttendeesJson = """[{"name":"Bob","email":"bob@x.test","response":"accepted","optional":false}]""";

        await using (var db = fx.CreateDbContext())
        {
            db.OutlookCalendarEvents.Add(ev);
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var store = new OutlookCalendarStore(db);
            var got = Assert.Single(await store.ListEventsAsync(
                userId, DateTimeOffset.Parse("2026-07-01T00:00:00Z"), DateTimeOffset.Parse("2026-07-03T00:00:00Z")));
            var attendee = Assert.Single(got.Attendees!);
            Assert.Equal("bob@x.test", attendee.Email);
            Assert.Equal("accepted", attendee.ResponseStatus);
        }
    }

    /// <summary>Two rows for the same occurrence on one device would let the sweep delete one copy and leave
    /// the other, so the database refuses it outright rather than relying on the upsert getting it right.</summary>
    [Fact]
    public async Task DuplicateUidWithinASource_IsRejected()
    {
        var (userId, sourceId) = await SeedSource();
        await using var db = fx.CreateDbContext();
        db.OutlookCalendarEvents.Add(Event(sourceId, userId, "uid-dup"));
        await db.SaveChangesAsync();

        // A different surrogate id, same (source, uid) pair - the unique index has to catch it.
        var clash = Event(sourceId, userId, "uid-dup");
        clash.Id = Guid.NewGuid();
        db.OutlookCalendarEvents.Add(clash);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>The same appointment synced from two machines is two independent rows - otherwise each
    /// device's sweep would delete the other's copy.</summary>
    [Fact]
    public async Task TheSameUidOnTwoDevices_AreSeparateRows()
    {
        var (userId, firstSource) = await SeedSource("dev-a");
        Guid secondSource;
        await using (var db = fx.CreateDbContext())
        {
            var source = new OutlookCalendarSource
            {
                Id = Guid.NewGuid(), UserId = userId, DeviceId = "dev-b", DisplayName = "Outlook (HOME-PC)",
            };
            db.OutlookCalendarSources.Add(source);
            await db.SaveChangesAsync();
            secondSource = source.Id;
        }

        await using (var db = fx.CreateDbContext())
        {
            db.OutlookCalendarEvents.Add(Event(firstSource, userId, "shared-uid"));
            db.OutlookCalendarEvents.Add(Event(secondSource, userId, "shared-uid"));
            await db.SaveChangesAsync();
        }

        await using (var readBack = fx.CreateDbContext())
        {
            Assert.Equal(2, await readBack.OutlookCalendarEvents.CountAsync(e => e.UserId == userId));
        }
    }

    [Fact]
    public async Task DeletingASource_CascadesItsEvents()
    {
        var (userId, sourceId) = await SeedSource();
        await using (var db = fx.CreateDbContext())
        {
            db.OutlookCalendarEvents.Add(Event(sourceId, userId, "uid-cascade"));
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.OutlookCalendarSources.Remove(await db.OutlookCalendarSources.SingleAsync(s => s.Id == sourceId));
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            Assert.Empty(await db.OutlookCalendarEvents.Where(e => e.SourceId == sourceId).ToListAsync());
        }
    }

    /// <summary>Deleting the account has to erase the mirror too - an event cascades from its user as well as
    /// its source, and a wrongly configured pair would either block the delete or leave rows behind.</summary>
    [Fact]
    public async Task DeletingTheUser_CascadesSourcesAndEvents()
    {
        var (userId, sourceId) = await SeedSource();
        await using (var db = fx.CreateDbContext())
        {
            db.OutlookCalendarEvents.Add(Event(sourceId, userId, "uid-user-cascade"));
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == userId));
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            Assert.Empty(await db.OutlookCalendarSources.Where(s => s.UserId == userId).ToListAsync());
            Assert.Empty(await db.OutlookCalendarEvents.Where(e => e.UserId == userId).ToListAsync());
        }
    }
}
