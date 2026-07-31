using Diariz.Api.Services;
using Sentry;   // SentryEvent; reaches the test project transitively via the API project reference

namespace Diariz.Api.Tests;

public class SentryScrubberTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Worker-Secret")]
    [InlineData("Cookie")]
    [InlineData("password")]
    [InlineData("ApiKey")]
    [InlineData("Summarization__ApiKey")]
    public void IsSensitiveKey_MatchesCredentials(string key)
    {
        Assert.True(SentryScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("transcript")]
    [InlineData("segments")]
    [InlineData("summary")]
    [InlineData("minutes")]
    public void IsSensitiveKey_MatchesMeetingContent(string key)
    {
        Assert.True(SentryScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("recordingId")]
    [InlineData("transcriptionId")]
    [InlineData("blobKey")]
    [InlineData("userId")]
    [InlineData("model")]
    public void IsSensitiveKey_KeepsTheIdentifiersNeededToDiagnose(string key)
    {
        // A scrubber that redacts everything is useless.
        Assert.False(SentryScrubber.IsSensitiveKey(key));
    }

    [Fact]
    public void Scrub_RedactsSensitiveExtras_AndKeepsIdentifiers()
    {
        var e = new SentryEvent();
        e.SetExtra("transcriptionId", "tid-1");
        e.SetExtra("text", "the confidential meeting content");

        var cleaned = SentryScrubber.Scrub(e);

        Assert.NotNull(cleaned);
        Assert.Equal("tid-1", cleaned!.Extra["transcriptionId"]);
        Assert.Equal(SentryScrubber.Redacted, cleaned.Extra["text"]);
    }

    // --- Fix round 1 -----------------------------------------------------------------------
    // Critical 1: the query string is one opaque string (not a named field a deny-list can
    // reach) and CLAUDE.md documents that the SignalR hub takes its JWT as a query parameter
    // (`/hubs/transcription?access_token=<JWT>` - browsers can't set an Authorization header on
    // a WebSocket handshake). Any captured event on that path must not carry the live token.

    [Fact]
    public void Scrub_RemovesTheQueryString_EvenWhenItCarriesABearerToken()
    {
        var e = new SentryEvent();
        e.Request.QueryString = "access_token=eyJhbGciOiJIUzI1NiJ9.a-live-jwt.signature";

        var cleaned = SentryScrubber.Scrub(e);

        Assert.NotNull(cleaned);
        Assert.Null(cleaned!.Request.QueryString);
    }

    [Fact]
    public void Scrub_RemovesCookies()
    {
        var e = new SentryEvent();
        e.Request.Cookies = "session=abc123";

        var cleaned = SentryScrubber.Scrub(e);

        Assert.Null(cleaned!.Request.Cookies);
    }

    [Fact]
    public void Scrub_StripsTheQueryPortionOfUrl_ButKeepsThePathForDiagnosis()
    {
        var e = new SentryEvent();
        e.Request.Url = "https://api.example/hubs/transcription?access_token=eyJhbGciOiJIUzI1NiJ9.secret";

        var cleaned = SentryScrubber.Scrub(e);

        Assert.Equal("https://api.example/hubs/transcription", cleaned!.Request.Url);
    }

    [Fact]
    public void Scrub_LeavesAUrlWithNoQueryStringUnchanged()
    {
        var e = new SentryEvent();
        e.Request.Url = "https://api.example/api/recordings/123";

        var cleaned = SentryScrubber.Scrub(e);

        Assert.Equal("https://api.example/api/recordings/123", cleaned!.Request.Url);
    }

    // Important 3: Tags is the same shape as Extra and got no redaction, with no stated reason
    // for the asymmetry.

    [Fact]
    public void Scrub_RedactsSensitiveTags_AndKeepsIdentifiers()
    {
        var e = new SentryEvent();
        e.SetTag("transcriptionId", "tid-1");
        e.SetTag("summary", "the confidential meeting content");

        var cleaned = SentryScrubber.Scrub(e);

        Assert.Equal("tid-1", cleaned!.Tags["transcriptionId"]);
        Assert.Equal(SentryScrubber.Redacted, cleaned.Tags["summary"]);
    }

    // Critical 2: Sentry.Extensions.Logging (shipped inside Sentry.AspNetCore, on by default)
    // turns every ILogger call at Information+ into a breadcrumb carrying a Data dictionary of
    // the same shape as Extra. Investigated via reflection against the installed Sentry 6.8.0
    // assembly: Breadcrumb.Data's setter exists but is non-public (private, `init`-only), and
    // SentryEvent.Breadcrumbs is a read-only collection with no replace/index API - so a
    // breadcrumb already attached to an event cannot be redacted from Scrub(SentryEvent). The
    // SDK provides a separate hook for this: SentryOptions.SetBeforeBreadcrumb(Func<Breadcrumb,
    // Breadcrumb?>) runs before a breadcrumb is frozen into the event and can return a
    // replacement built via Breadcrumb's public constructor. Task 7 wires
    // SentryScrubber.ScrubBreadcrumb to that hook, the same way it wires Scrub to SetBeforeSend.

    [Fact]
    public void ScrubBreadcrumb_RedactsSensitiveData_AndKeepsIdentifiers()
    {
        var breadcrumb = new Breadcrumb(
            message: "Summarization complete",
            type: "default",
            data: new Dictionary<string, string>
            {
                ["transcriptionId"] = "tid-1",
                ["summary"] = "the confidential meeting content",
            });

        var cleaned = SentryScrubber.ScrubBreadcrumb(breadcrumb);

        Assert.NotNull(cleaned);
        Assert.Equal("tid-1", cleaned!.Data!["transcriptionId"]);
        Assert.Equal(SentryScrubber.Redacted, cleaned.Data!["summary"]);
    }

    [Fact]
    public void ScrubBreadcrumb_DoesNotThrow_WhenDataIsNull()
    {
        var breadcrumb = new Breadcrumb(message: "started", type: "default");

        var cleaned = SentryScrubber.ScrubBreadcrumb(breadcrumb);

        Assert.NotNull(cleaned);
        Assert.Null(cleaned!.Data);
    }

    [Fact]
    public void Scrub_DoesNotThrow_WhenTheEventHasBreadcrumbsAttached()
    {
        var e = new SentryEvent();
        e.AddBreadcrumb(new Breadcrumb(message: "started", type: "default"));

        var cleaned = SentryScrubber.Scrub(e);

        Assert.NotNull(cleaned);
        Assert.Single(cleaned!.Breadcrumbs);
    }

    // Constraint: Scrub runs inside the SDK's send path and must not throw on a minimal event.

    [Fact]
    public void Scrub_DoesNotThrow_OnABareEvent()
    {
        var cleaned = SentryScrubber.Scrub(new SentryEvent());

        Assert.NotNull(cleaned);
    }
}
