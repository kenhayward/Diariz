using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres coverage for external <c>.ics</c> feeds: the CRUD round-trip through the controller
/// and the cascade that removes a user's feeds when the user is deleted (the in-memory provider doesn't
/// enforce FKs, so this is verified here).</summary>
[Collection(IntegrationCollection.Name)]
public class IcsCalendarFeedsIntegrationTests(ContainersFixture fx)
{
    // Probe always succeeds - the SSRF/network path is unit-tested; here we exercise persistence.
    private sealed class OkProbeClient : IIcsCalendarClient
    {
        public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);
        public Task<(bool Ok, string? Error)> ProbeAsync(string url, CancellationToken ct = default) =>
            Task.FromResult((true, (string?)null));
    }

    private static CalendarFeedsController Controller(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db, new OkProbeClient()) { ControllerContext = Http.Context(userId) };

    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Create_List_Update_Delete_RoundTrip()
    {
        var userId = await SeedUser();
        Guid feedId;

        await using (var db = fx.CreateDbContext())
        {
            var created = (await Controller(db, userId)
                .Create(new IcsFeedRequest("Team", "https://feeds.example.com/team.ics", "#7986CB"), default)).Value!;
            feedId = created.Id;
            Assert.Equal("Team", created.Name);
        }

        await using (var db = fx.CreateDbContext())
        {
            var feeds = await Controller(db, userId).List();
            Assert.Equal("Team", Assert.Single(feeds).Name);
        }

        await using (var db = fx.CreateDbContext())
        {
            var updated = (await Controller(db, userId)
                .Update(feedId, new IcsFeedRequest("Team (renamed)", "https://feeds.example.com/team.ics", Enabled: false), default)).Value!;
            Assert.Equal("Team (renamed)", updated.Name);
            Assert.False(updated.Enabled);
        }

        await using (var db = fx.CreateDbContext())
        {
            Assert.IsType<NoContentResult>(await Controller(db, userId).Delete(feedId, default));
        }

        await using (var verify = fx.CreateDbContext())
            Assert.False(await verify.IcsCalendarSources.AnyAsync(s => s.Id == feedId));
    }

    [Fact]
    public async Task DeletingUser_CascadesFeeds()
    {
        var userId = await SeedUser();
        await using (var db = fx.CreateDbContext())
            await Controller(db, userId).Create(new IcsFeedRequest("Team", "https://feeds.example.com/team.ics"), default);

        await using (var db = fx.CreateDbContext())
        {
            var user = await db.Users.FindAsync(userId);
            db.Users.Remove(user!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.IcsCalendarSources.AnyAsync(s => s.UserId == userId));
    }
}
