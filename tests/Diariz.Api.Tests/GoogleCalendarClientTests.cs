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
    public async Task GetEventAsync_FetchesById_AndParsesTheSingleEvent()
    {
        const string json = """
        { "id": "evt1", "summary": "1:1", "htmlLink": "https://cal/evt1",
          "start": { "dateTime": "2026-07-02T09:00:00Z" }, "end": { "dateTime": "2026-07-02T09:30:00Z" } }
        """;
        var handler = new FakeHttpMessageHandler(json);
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("cal-tok"));

        var e = await client.GetEventAsync(Guid.NewGuid(), "evt1");

        Assert.Equal("evt1", e!.Id);
        Assert.Equal("1:1", e.Summary);
        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("calendar/v3/calendars/primary/events/evt1", url);
        Assert.Equal("cal-tok", handler.LastRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task GetEventAsync_ReturnsNull_WhenEventNotFound()
    {
        var handler = new FakeHttpMessageHandler("{\"error\":\"notFound\"}", HttpStatusCode.NotFound);
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
    public async Task ListEventsAsync_SendsBearerAndTimeWindow()
    {
        var handler = new FakeHttpMessageHandler(
            "{ \"items\": [ { \"id\": \"e1\", \"start\": { \"dateTime\": \"2026-07-02T09:00:00Z\" }, \"end\": { \"dateTime\": \"2026-07-02T10:00:00Z\" } } ] }");
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("cal-tok"));

        var events = await client.ListEventsAsync(Guid.NewGuid(), At("2026-07-02T08:30:00Z"), At("2026-07-02T10:30:00Z"));

        Assert.Single(events!);
        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("calendar/v3/calendars/primary/events", url);
        Assert.Contains("singleEvents=true", url);
        Assert.Contains("timeMin=2026-07-02T08%3A30%3A00Z", url);
        Assert.Contains("timeMax=2026-07-02T10%3A30%3A00Z", url);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("cal-tok", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ListEventsAsync_Throws_OnCalendarError()
    {
        var handler = new FakeHttpMessageHandler("{\"error\":\"rateLimitExceeded\"}", HttpStatusCode.TooManyRequests);
        var client = new GoogleCalendarClient(new HttpClient(handler), new StubTokenProvider("tok"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListEventsAsync(Guid.NewGuid(), At("2026-07-02T09:00:00Z"), At("2026-07-02T10:00:00Z")));

        Assert.Contains("429", ex.Message);
        Assert.Contains("rateLimitExceeded", ex.Message);
    }
}
