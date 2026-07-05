using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class CalendarFeedsControllerTests
{
    // Probes succeed unless we hand it an error to return; records the last probed URL.
    private sealed class FakeIcsClient : IIcsCalendarClient
    {
        public (bool Ok, string? Error) ProbeResult { get; set; } = (true, null);
        public string? ProbedUrl { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);

        public Task<(bool Ok, string? Error)> ProbeAsync(string url, CancellationToken ct = default)
        {
            ProbedUrl = url;
            return Task.FromResult(ProbeResult);
        }
    }

    private static CalendarFeedsController Build(DiarizDbContext db, Guid userId, FakeIcsClient ics) =>
        new(db, ics) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task Create_ProbesUrl_AndStoresFeed()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var ics = new FakeIcsClient();
        var controller = Build(db, user, ics);

        var result = await controller.Create(new IcsFeedRequest("Team", "https://x.example.com/t.ics", "#7986CB"), default);

        var dto = Assert.IsType<IcsFeedDto>(result.Value);
        Assert.Equal("Team", dto.Name);
        Assert.Equal("#7986CB", dto.Color);
        Assert.True(dto.Enabled);
        Assert.Equal("https://x.example.com/t.ics", ics.ProbedUrl);
        Assert.Equal(1, await db.IcsCalendarSources.CountAsync(s => s.UserId == user));
    }

    [Fact]
    public async Task Create_WhenProbeFails_ReturnsBadRequest_AndStoresNothing()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var ics = new FakeIcsClient { ProbeResult = (false, "That URL did not return a calendar feed.") };
        var controller = Build(db, user, ics);

        var result = await controller.Create(new IcsFeedRequest("Bad", "https://x.example.com/nope"), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, await db.IcsCalendarSources.CountAsync());
    }

    [Fact]
    public async Task Create_WhenNameBlank_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid(), new FakeIcsClient());

        var result = await controller.Create(new IcsFeedRequest("  ", "https://x.example.com/t.ics"), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task List_ReturnsOnlyTheCallersFeeds()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.IcsCalendarSources.AddRange(
            new IcsCalendarSource { Id = Guid.NewGuid(), UserId = me, Name = "Mine", Url = "https://a/x.ics" },
            new IcsCalendarSource { Id = Guid.NewGuid(), UserId = other, Name = "Theirs", Url = "https://b/x.ics" });
        await db.SaveChangesAsync();

        var feeds = await Build(db, me, new FakeIcsClient()).List();

        Assert.Equal("Mine", Assert.Single(feeds).Name);
    }

    [Fact]
    public async Task Update_RenameOnly_DoesNotReprobe()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var feed = new IcsCalendarSource { Id = Guid.NewGuid(), UserId = user, Name = "Old", Url = "https://a/x.ics", Enabled = true };
        db.IcsCalendarSources.Add(feed);
        await db.SaveChangesAsync();
        var ics = new FakeIcsClient();

        var result = await Build(db, user, ics).Update(feed.Id, new IcsFeedRequest("New", "https://a/x.ics", Enabled: false), default);

        var dto = Assert.IsType<IcsFeedDto>(result.Value);
        Assert.Equal("New", dto.Name);
        Assert.False(dto.Enabled);
        Assert.Null(ics.ProbedUrl); // URL unchanged → no re-probe
    }

    [Fact]
    public async Task Update_ChangedUrl_ReprobesAndClearsError()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var feed = new IcsCalendarSource
        {
            Id = Guid.NewGuid(), UserId = user, Name = "F", Url = "https://a/x.ics", LastError = "was broken",
        };
        db.IcsCalendarSources.Add(feed);
        await db.SaveChangesAsync();
        var ics = new FakeIcsClient();

        var result = await Build(db, user, ics).Update(feed.Id, new IcsFeedRequest("F", "https://a/y.ics"), default);

        var dto = Assert.IsType<IcsFeedDto>(result.Value);
        Assert.Equal("https://a/y.ics", dto.Url);
        Assert.Null(dto.LastError);
        Assert.Equal("https://a/y.ics", ics.ProbedUrl);
    }

    [Fact]
    public async Task Update_OtherUsersFeed_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var feed = new IcsCalendarSource { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "F", Url = "https://a/x.ics" };
        db.IcsCalendarSources.Add(feed);
        await db.SaveChangesAsync();

        var result = await Build(db, Guid.NewGuid(), new FakeIcsClient())
            .Update(feed.Id, new IcsFeedRequest("F", "https://a/x.ics"), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Delete_RemovesOwnedFeed()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var feed = new IcsCalendarSource { Id = Guid.NewGuid(), UserId = user, Name = "F", Url = "https://a/x.ics" };
        db.IcsCalendarSources.Add(feed);
        await db.SaveChangesAsync();

        var result = await Build(db, user, new FakeIcsClient()).Delete(feed.Id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, await db.IcsCalendarSources.CountAsync());
    }

    [Fact]
    public async Task Delete_OtherUsersFeed_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var feed = new IcsCalendarSource { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "F", Url = "https://a/x.ics" };
        db.IcsCalendarSources.Add(feed);
        await db.SaveChangesAsync();

        var result = await Build(db, Guid.NewGuid(), new FakeIcsClient()).Delete(feed.Id, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(1, await db.IcsCalendarSources.CountAsync());
    }
}
