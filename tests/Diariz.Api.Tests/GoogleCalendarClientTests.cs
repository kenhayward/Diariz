using System.Net;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

public class GoogleCalendarClientTests
{
    private sealed class StubTokenProvider(string? token) : IGoogleTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(token);
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso);

    private const string OneEventBody =
        "{ \"items\": [ { \"id\": \"e1\", \"start\": { \"dateTime\": \"2026-07-02T09:00:00Z\" }, \"end\": { \"dateTime\": \"2026-07-02T10:00:00Z\" } } ] }";

    /// <summary>Dispatches each request by URL so a multi-call flow (calendarList → per-calendar events) can be
    /// faked; the client now reads all the user's selected calendars, not a single fixed endpoint.</summary>
    private sealed class RoutingHandler(Func<HttpRequestMessage, (HttpStatusCode status, string body)> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = route(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static bool IsCalendarList(HttpRequestMessage req) => req.RequestUri!.AbsolutePath.Contains("calendarList");

    // ---- ParseEvents ----

    [Fact]
    public void ParseEvents_ReadsTimedEvents_WithSummaryAndLink()
    {
        const string json = """
        { "items": [
          { "id": "e1", "summary": "Standup", "htmlLink": "https://cal/e1",
            "start": { "dateTime": "2026-07-02T09:00:00Z" }, "end": { "dateTime": "2026-07-02T09:30:00Z" } }
        ] }
        """;

        var e = Assert.Single(GoogleCalendarClient.ParseEvents(json));
        Assert.Equal("e1", e.Id);
        Assert.Equal("Standup", e.Summary);
        Assert.Equal("https://cal/e1", e.HtmlLink);
        Assert.Equal(At("2026-07-02T09:00:00Z"), e.Start);
        Assert.Equal(At("2026-07-02T09:30:00Z"), e.End);
    }

    [Fact]
    public void ParseEvents_HandlesAllDayEvents_AndSkipsUnparseable()
    {
        const string json = """
        { "items": [
          { "id": "allday", "start": { "date": "2026-07-02" }, "end": { "date": "2026-07-03" } },
          { "id": "broken", "summary": "no times" }
        ] }
        """;

        var events = GoogleCalendarClient.ParseEvents(json);
        var e = Assert.Single(events); // the broken one is skipped
        Assert.Equal("allday", e.Id);
        Assert.Null(e.Summary);
    }

    [Fact]
    public void ParseEvents_EmptyOrMissingItems_ReturnsEmpty()
    {
        Assert.Empty(GoogleCalendarClient.ParseEvents("{}"));
        Assert.Empty(GoogleCalendarClient.ParseEvents("{ \"items\": [] }"));
    }

    [Fact]
    public void ParseEvents_ReadsRichFields_DescriptionLocationOrganizerAttendees()
    {
        const string json = """
        { "items": [
          { "id": "e1", "summary": "Planning", "location": "Room 4 / meet.google.com/abc",
            "description": "Quarterly planning agenda.",
            "start": { "dateTime": "2026-07-02T09:00:00Z" }, "end": { "dateTime": "2026-07-02T10:00:00Z" },
            "organizer": { "email": "boss@x.test", "displayName": "The Boss" },
            "attendees": [
              { "email": "boss@x.test", "displayName": "The Boss", "responseStatus": "accepted", "organizer": true },
              { "email": "me@x.test", "responseStatus": "needsAction", "self": true }
            ] }
        ] }
        """;

        var e = Assert.Single(GoogleCalendarClient.ParseEvents(json));
        Assert.Equal("Quarterly planning agenda.", e.Description);
        Assert.Equal("Room 4 / meet.google.com/abc", e.Location);
        Assert.Equal("boss@x.test", e.Organizer!.Email);
        Assert.Equal("The Boss", e.Organizer.DisplayName);
        Assert.Equal(2, e.Attendees!.Count);
        Assert.True(e.Attendees[0].Organizer);
        Assert.Equal("accepted", e.Attendees[0].ResponseStatus);
        Assert.True(e.Attendees[1].Self);
        Assert.Equal("needsAction", e.Attendees[1].ResponseStatus);
    }

    // ---- GetEventAsync ----

    [Fact]
    public async Task GetEventAsync_ReturnsNull_WhenNoAccessToken()
    {
        var handler = new FakeHttpMessageHandler("{}");
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider(null));

        Assert.Null(await client.GetEventAsync(Guid.NewGuid(), "evt1"));
        Assert.Null(handler.LastRequest); // never called Calendar
    }

    [Fact]
    public async Task GetEventAsync_SearchesCalendars_AndTagsTheMatch()
    {
        const string calList = "{ \"items\": [ { \"id\": \"primary\", \"summary\": \"Me\", \"backgroundColor\": \"#4285F4\", \"primary\": true, \"selected\": true } ] }";
        const string eventJson = """
        { "id": "evt1", "summary": "1:1", "htmlLink": "https://cal/evt1",
          "start": { "dateTime": "2026-07-02T09:00:00Z" }, "end": { "dateTime": "2026-07-02T09:30:00Z" } }
        """;
        HttpRequestMessage? getReq = null;
        var handler = new RoutingHandler(req =>
        {
            if (IsCalendarList(req)) return (HttpStatusCode.OK, calList);
            getReq = req;
            return (HttpStatusCode.OK, eventJson);
        });
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("cal-tok"));

        var e = await client.GetEventAsync(Guid.NewGuid(), "evt1");

        Assert.Equal("evt1", e!.Id);
        Assert.Equal("1:1", e.Summary);
        Assert.Equal("primary", e.CalendarId); // tagged with the calendar it was found on
        Assert.Equal("#4285F4", e.Color);
        Assert.Contains("/calendars/primary/events/evt1", getReq!.RequestUri!.ToString());
        Assert.Equal("cal-tok", getReq.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task GetEventAsync_ReturnsNull_WhenNotFoundOnAnyCalendar()
    {
        const string calList = "{ \"items\": [ { \"id\": \"primary\", \"primary\": true, \"selected\": true } ] }";
        var handler = new RoutingHandler(req =>
            IsCalendarList(req) ? (HttpStatusCode.OK, calList) : (HttpStatusCode.NotFound, "{\"error\":\"notFound\"}"));
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("tok"));

        Assert.Null(await client.GetEventAsync(Guid.NewGuid(), "gone"));
    }

    // ---- PickBest ----

    [Fact]
    public void PickBest_ChoosesTheMostOverlappingEvent()
    {
        var events = new List<CalendarEvent>
        {
            new("a", "Barely", At("2026-07-02T08:00:00Z"), At("2026-07-02T09:05:00Z"), null),
            new("b", "Main", At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"), null),
        };
        // Recording 09:00–10:00: 'b' overlaps a full hour, 'a' only 5 min.
        var best = GoogleCalendarClient.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"));
        Assert.Equal("b", best!.Id);
    }

    [Fact]
    public void PickBest_ReturnsNull_WhenNothingOverlaps()
    {
        var events = new List<CalendarEvent>
        {
            new("x", "Earlier", At("2026-07-02T06:00:00Z"), At("2026-07-02T07:00:00Z"), null),
        };
        Assert.Null(GoogleCalendarClient.PickBest(events, At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z")));
    }

    // ---- ListEventsAsync ----

    [Fact]
    public async Task ListEventsAsync_ReturnsNull_WhenNoAccessToken()
    {
        var handler = new FakeHttpMessageHandler("{}");
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider(null));

        var events = await client.ListEventsAsync(Guid.NewGuid(), At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z"));

        Assert.Null(events);
        Assert.Null(handler.LastRequest); // never called Calendar
    }

    [Fact]
    public async Task ListEventsAsync_SendsBearerAndTimeWindowToEachSelectedCalendar()
    {
        const string calList = "{ \"items\": [ { \"id\": \"primary\", \"summary\": \"Me\", \"primary\": true, \"selected\": true } ] }";
        HttpRequestMessage? eventsReq = null;
        var handler = new RoutingHandler(req =>
        {
            if (IsCalendarList(req)) return (HttpStatusCode.OK, calList);
            eventsReq = req;
            return (HttpStatusCode.OK, OneEventBody);
        });
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("cal-tok"));

        var events = await client.ListEventsAsync(Guid.NewGuid(), At("2026-07-02T08:30:00Z"), At("2026-07-02T10:30:00Z"));

        Assert.Single(events!);
        var url = eventsReq!.RequestUri!.ToString();
        Assert.Contains("/calendars/primary/events", url);
        Assert.Contains("singleEvents=true", url);
        Assert.Contains("timeMin=2026-07-02T08%3A30%3A00Z", url);
        Assert.Contains("timeMax=2026-07-02T10%3A30%3A00Z", url);
        Assert.Equal("cal-tok", eventsReq.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ListEventsAsync_MergesSelectedCalendars_TaggedWithColour_SkippingUnselected()
    {
        const string calList = """
        { "items": [
          { "id": "primary", "summary": "Me", "backgroundColor": "#4285F4", "primary": true, "selected": true },
          { "id": "team@group.calendar.google.com", "summary": "Team", "backgroundColor": "#0B8043", "selected": true },
          { "id": "muted@x", "summary": "Muted", "backgroundColor": "#999999", "selected": false }
        ] }
        """;
        const string primaryEvents = "{ \"items\": [ { \"id\": \"e-me\", \"start\": { \"dateTime\": \"2026-07-02T09:00:00Z\" }, \"end\": { \"dateTime\": \"2026-07-02T09:30:00Z\" } } ] }";
        const string teamEvents = "{ \"items\": [ { \"id\": \"e-team\", \"start\": { \"dateTime\": \"2026-07-02T08:00:00Z\" }, \"end\": { \"dateTime\": \"2026-07-02T08:30:00Z\" } } ] }";

        var fetched = new System.Collections.Concurrent.ConcurrentBag<string>();
        var handler = new RoutingHandler(req =>
        {
            if (IsCalendarList(req)) return (HttpStatusCode.OK, calList);
            var calId = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.Split("/calendars/")[1].Split("/events")[0]);
            fetched.Add(calId);
            return calId == "primary" ? (HttpStatusCode.OK, primaryEvents)
                : calId.StartsWith("team") ? (HttpStatusCode.OK, teamEvents)
                : (HttpStatusCode.OK, "{ \"items\": [] }");
        });
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("tok"));

        var events = (await client.ListEventsAsync(Guid.NewGuid(), At("2026-07-02T00:00:00Z"), At("2026-07-03T00:00:00Z")))!;

        // Merged + ordered by start (team 08:00 before me 09:00), each tagged with its calendar's colour.
        Assert.Equal(["e-team", "e-me"], events.Select(e => e.Id));
        Assert.Equal("#0B8043", events[0].Color);
        Assert.Equal("team@group.calendar.google.com", events[0].CalendarId);
        Assert.Equal("Team", events[0].CalendarName);
        Assert.Equal("#4285F4", events[1].Color);
        Assert.DoesNotContain("muted@x", fetched); // the unselected calendar was never fetched
    }

    [Fact]
    public async Task ListEventsAsync_Throws_WhenCalendarListFails()
    {
        var handler = new FakeHttpMessageHandler("{\"error\":\"rateLimitExceeded\"}", HttpStatusCode.TooManyRequests);
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("tok"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListEventsAsync(Guid.NewGuid(), At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z")));

        Assert.Contains("429", ex.Message);
        Assert.Contains("rateLimitExceeded", ex.Message);
    }

    // ---- ParseCalendarList ----

    [Fact]
    public void ParseCalendarList_ReadsIdSummaryColoursAndFlags()
    {
        const string json = """
        { "items": [
          { "id": "primary", "summary": "Me", "backgroundColor": "#4285F4", "foregroundColor": "#1d1d1d", "primary": true, "selected": true },
          { "id": "team@g", "summary": "Team", "backgroundColor": "#0B8043", "selected": false }
        ] }
        """;
        var entries = GoogleCalendarClient.ParseCalendarList(json);

        Assert.Equal(2, entries.Count);
        Assert.Equal("primary", entries[0].Id);
        Assert.Equal("#4285F4", entries[0].BackgroundColor);
        Assert.True(entries[0].Primary);
        Assert.True(entries[0].Selected);
        Assert.False(entries[1].Selected);
        Assert.False(entries[1].Primary);
    }

    [Fact]
    public void ParseCalendarList_EmptyOrMissing_ReturnsEmpty()
    {
        Assert.Empty(GoogleCalendarClient.ParseCalendarList("{}"));
        Assert.Empty(GoogleCalendarClient.ParseCalendarList("{ \"items\": [] }"));
    }
}
