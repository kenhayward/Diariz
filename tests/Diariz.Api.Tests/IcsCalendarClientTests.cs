using System.Net;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>The external-feed calendar client: fetches each of the user's enabled <c>.ics</c> feeds behind the
/// SSRF guard, parses them, and merges the events (ids prefixed <c>ics:{sourceId}:</c>). The HTTP boundary is a
/// fake handler and DNS is a stub, so no real network/DNS is touched.</summary>
public class IcsCalendarClientTests
{
    private const string TeamIcs = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:team-1@test
        SUMMARY:Team sync
        DTSTART:20260210T090000Z
        DTEND:20260210T093000Z
        END:VEVENT
        END:VCALENDAR
        """;

    private const string OpsIcs = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:ops-1@test
        SUMMARY:Ops review
        DTSTART:20260211T140000Z
        DTEND:20260211T150000Z
        END:VEVENT
        END:VCALENDAR
        """;

    private static readonly DateTimeOffset Min = new(2026, 2, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Max = new(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);

    // Routes a request to a canned (status, body) by URL; records which URLs were hit.
    private sealed class RouteHandler(Func<string, (HttpStatusCode, string)> route) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, body) = route(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    // DNS stub: everything resolves to a public IP unless the host is in `blocked`.
    private static Func<string, CancellationToken, Task<IPAddress[]>> Dns(params string[] blockedHosts) =>
        (host, ct) => Task.FromResult(new[]
        {
            blockedHosts.Contains(host) ? IPAddress.Parse("10.0.0.5") : IPAddress.Parse("8.8.8.8"),
        });

    private static IcsCalendarSource Feed(Guid userId, string name, string url, string? color = "#7986CB", bool enabled = true) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Name = name, Url = url, Color = color, Enabled = enabled };

    [Fact]
    public async Task ListEventsAsync_MergesEnabledFeeds_TagsAndPrefixesIds()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var team = Feed(user, "Team", "https://team.example.com/team.ics", "#7986CB");
        var ops = Feed(user, "Ops", "https://ops.example.com/ops.ics", "#33B679");
        db.IcsCalendarSources.AddRange(team, ops);
        await db.SaveChangesAsync();

        var handler = new RouteHandler(url => url.Contains("team") ? (HttpStatusCode.OK, TeamIcs) : (HttpStatusCode.OK, OpsIcs));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance, Dns());

        var events = await client.ListEventsAsync(user, Min, Max);

        Assert.Equal(2, events.Count);
        var teamEvent = events.Single(e => e.Summary == "Team sync");
        Assert.Equal($"ics:{team.Id}:team-1@test", teamEvent.Id);
        Assert.Equal($"ics:{team.Id}", teamEvent.CalendarId);
        Assert.Equal("Team", teamEvent.CalendarName);
        Assert.Equal("#7986CB", teamEvent.Color);
        Assert.Contains(events, e => e.Summary == "Ops review" && e.Color == "#33B679");
        // Ordered by start.
        Assert.Equal("Team sync", events[0].Summary);
    }

    [Fact]
    public async Task ListEventsAsync_MarksFetchedFeeds_Healthy()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var team = Feed(user, "Team", "https://team.example.com/team.ics");
        db.IcsCalendarSources.Add(team);
        await db.SaveChangesAsync();

        var handler = new RouteHandler(_ => (HttpStatusCode.OK, TeamIcs));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance, Dns());

        await client.ListEventsAsync(user, Min, Max);

        var refreshed = await db.IcsCalendarSources.FindAsync(team.Id);
        Assert.NotNull(refreshed!.LastFetchedAt);
        Assert.Null(refreshed.LastError);
    }

    [Fact]
    public async Task ListEventsAsync_SkipsDisabledFeeds()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        db.IcsCalendarSources.Add(Feed(user, "Off", "https://off.example.com/x.ics", enabled: false));
        await db.SaveChangesAsync();

        var handler = new RouteHandler(_ => (HttpStatusCode.OK, TeamIcs));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance, Dns());

        var events = await client.ListEventsAsync(user, Min, Max);

        Assert.Empty(events);
        Assert.Empty(handler.Requested); // disabled feed never fetched
    }

    [Fact]
    public async Task ListEventsAsync_RecordsError_OnFetchFailure_ButKeepsOtherFeeds()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var bad = Feed(user, "Bad", "https://bad.example.com/404.ics");
        var team = Feed(user, "Team", "https://team.example.com/team.ics");
        db.IcsCalendarSources.AddRange(bad, team);
        await db.SaveChangesAsync();

        var handler = new RouteHandler(url => url.Contains("bad") ? (HttpStatusCode.NotFound, "nope") : (HttpStatusCode.OK, TeamIcs));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance, Dns());

        var events = await client.ListEventsAsync(user, Min, Max);

        Assert.Equal("Team sync", Assert.Single(events).Summary); // good feed still returned
        var badRow = await db.IcsCalendarSources.FindAsync(bad.Id);
        Assert.NotNull(badRow!.LastError);
    }

    [Fact]
    public async Task ListEventsAsync_BlocksFeedResolvingToPrivateAddress()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var evil = Feed(user, "Evil", "https://evil.example.com/x.ics");
        db.IcsCalendarSources.Add(evil);
        await db.SaveChangesAsync();

        var handler = new RouteHandler(_ => (HttpStatusCode.OK, TeamIcs));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance,
            Dns("evil.example.com")); // resolves to 10.0.0.5

        var events = await client.ListEventsAsync(user, Min, Max);

        Assert.Empty(events);
        Assert.Empty(handler.Requested); // never connected - blocked before the HTTP call
        var row = await db.IcsCalendarSources.FindAsync(evil.Id);
        Assert.NotNull(row!.LastError);
    }

    [Fact]
    public async Task ProbeAsync_RejectsNonHttpsUrl()
    {
        using var db = TestDb.Create();
        var handler = new RouteHandler(_ => (HttpStatusCode.OK, TeamIcs));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance, Dns());

        var (ok, error) = await client.ProbeAsync("http://team.example.com/x.ics");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task ProbeAsync_AcceptsAReachableParseableFeed()
    {
        using var db = TestDb.Create();
        var handler = new RouteHandler(_ => (HttpStatusCode.OK, TeamIcs));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance, Dns());

        var (ok, error) = await client.ProbeAsync("https://team.example.com/team.ics");

        Assert.True(ok, error);
        Assert.Null(error);
    }

    [Fact]
    public async Task ProbeAsync_ReportsUnreachableFeed()
    {
        using var db = TestDb.Create();
        var handler = new RouteHandler(_ => (HttpStatusCode.NotFound, "nope"));
        var client = new IcsCalendarClient(new StubFactory(handler), db, NullLogger<IcsCalendarClient>.Instance, Dns());

        var (ok, error) = await client.ProbeAsync("https://team.example.com/team.ics");

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
