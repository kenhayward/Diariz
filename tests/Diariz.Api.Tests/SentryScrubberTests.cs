using System.Text;
using Diariz.Api.Services;
using Sentry;   // SentryEvent; reaches the test project transitively via the API project reference
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

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

    // --- Fix round 2 -----------------------------------------------------------------------
    // Critical: BeforeSend does not run for transactions in the .NET SDK, so every scrub above
    // was bypassed on the transaction path - which fires on EVERY request at TracesSampleRate =
    // 1.0, not only when something throws. Confirmed by reflection against the installed Sentry
    // 6.8.0 assembly: SentryOptions exposes a separate
    // SetBeforeSendTransaction(Func<SentryTransaction, SentryTransaction?>); SentryTransaction
    // and SentryEvent BOTH implement IEventLike, which carries the settable Request plus
    // IHasTags/IHasExtra's Tags/SetTag/Extra/SetExtra - so one routine scrubs both shapes.
    // (On a transaction, Extra and Data are the same backing store, so redacting via the
    // interface's Extra covers Data too.)

    [Fact]
    public void ScrubTransaction_RemovesTheQueryString_EvenWhenItCarriesABearerToken()
    {
        var tx = new SentryTransaction("POST /api/recordings", "http.server");
        tx.Request.QueryString = "access_token=eyJhbGciOiJIUzI1NiJ9.a-live-jwt.signature";

        var cleaned = SentryScrubber.ScrubTransaction(tx);

        Assert.NotNull(cleaned);
        Assert.Null(cleaned!.Request.QueryString);
    }

    [Fact]
    public void ScrubTransaction_RemovesCookies()
    {
        var tx = new SentryTransaction("POST /api/recordings", "http.server");
        tx.Request.Cookies = "session=abc123";

        var cleaned = SentryScrubber.ScrubTransaction(tx);

        Assert.Null(cleaned!.Request.Cookies);
    }

    [Fact]
    public void ScrubTransaction_NullsTheRequestBody()
    {
        var tx = new SentryTransaction("POST /api/recordings", "http.server");
        tx.Request.Data = "a dictated meeting note";

        var cleaned = SentryScrubber.ScrubTransaction(tx);

        Assert.Null(cleaned!.Request.Data);
    }

    [Fact]
    public void ScrubTransaction_StripsTheQueryPortionOfUrl_ButKeepsThePathForDiagnosis()
    {
        var tx = new SentryTransaction("GET /hubs/transcription", "http.server");
        tx.Request.Url = "https://api.example/hubs/transcription?access_token=eyJhbGciOiJIUzI1NiJ9.secret";

        var cleaned = SentryScrubber.ScrubTransaction(tx);

        Assert.Equal("https://api.example/hubs/transcription", cleaned!.Request.Url);
    }

    [Fact]
    public void ScrubTransaction_RedactsSensitiveTagsAndExtras_AndKeepsIdentifiers()
    {
        var tx = new SentryTransaction("POST /api/recordings", "http.server");
        tx.SetTag("transcriptionId", "tid-1");
        tx.SetTag("summary", "the confidential meeting content");
        ((IEventLike)tx).SetExtra("text", "the confidential meeting content");
        ((IEventLike)tx).SetExtra("recordingId", "rid-1");

        var cleaned = SentryScrubber.ScrubTransaction(tx);

        Assert.Equal("tid-1", cleaned!.Tags["transcriptionId"]);
        Assert.Equal(SentryScrubber.Redacted, cleaned.Tags["summary"]);
        Assert.Equal(SentryScrubber.Redacted, cleaned.Data["text"]);
        Assert.Equal("rid-1", cleaned.Data["recordingId"]);
    }

    [Fact]
    public void ScrubTransaction_DoesNotThrow_OnABareTransaction()
    {
        var cleaned = SentryScrubber.ScrubTransaction(new SentryTransaction("GET /health", "http.server"));

        Assert.NotNull(cleaned);
    }

    // Span descriptions: Sentry's automatic IHttpClientFactory instrumentation names an outbound
    // span "<METHOD> <url>", and WebhookDeliveryWorker posts to user-supplied URLs - so a webhook
    // secret in a query string becomes a span description, leaking by the same mechanism as the
    // request query string above.

    [Theory]
    [InlineData("POST https://hooks.example/x?token=WEBHOOK_SECRET", "POST https://hooks.example/x")]
    [InlineData("https://hooks.example/x?token=WEBHOOK_SECRET", "https://hooks.example/x")]
    [InlineData("GET https://api.example/v1/chat/completions", "GET https://api.example/v1/chat/completions")]
    [InlineData("SELECT * FROM \"Recordings\" WHERE \"Id\" = $1", "SELECT * FROM \"Recordings\" WHERE \"Id\" = $1")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void ScrubSpanDescription_StripsQueryStringsFromUrlsAndLeavesEverythingElseAlone(
        string? description, string? expected)
    {
        Assert.Equal(expected, SentryScrubber.ScrubSpanDescription(description));
    }

    [Fact]
    public void ScrubTransaction_StripsTheQueryStringFromItsOwnDescription()
    {
        var tx = new SentryTransaction("POST /api/recordings", "http.server")
        {
            Description = "POST https://hooks.example/x?token=WEBHOOK_SECRET",
        };

        var cleaned = SentryScrubber.ScrubTransaction(tx);

        Assert.Equal("POST https://hooks.example/x", cleaned!.Description);
    }

    // The one test that proves the WIRING rather than the routine: it initialises the pinned SDK
    // with a capturing transport, hooks ScrubTransaction up exactly as Program.cs does, and runs a
    // real transaction with a real child span through it - then asserts on the bytes that would
    // have gone to GlitchTip. Without SetBeforeSendTransaction this envelope carries the JWT, the
    // cookie, the tag and the webhook secret verbatim.
    [Fact]
    public async Task SetBeforeSendTransaction_ScrubsTheEnvelopeThatWouldReachTheServer()
    {
        var transport = new CapturingTransport();

        using (SentrySdk.Init(o =>
        {
            o.Dsn = "https://key@localhost/1";
            o.Transport = transport;
            o.TracesSampleRate = 1.0;
            o.AutoSessionTracking = false;
            o.SendDefaultPii = false;
            o.SetBeforeSendTransaction(SentryScrubber.ScrubTransaction);
        }))
        {
            var tx = SentrySdk.StartTransaction("POST /api/recordings", "http.server");
            SentrySdk.ConfigureScope(scope =>
            {
                scope.Transaction = tx;
                scope.Request.QueryString = "access_token=A_LIVE_JWT";
                scope.Request.Url = "https://api.example/hubs/transcription?access_token=A_LIVE_JWT";
                scope.Request.Cookies = "session=A_SESSION_COOKIE";
                scope.Request.Data = "a dictated meeting note";
                scope.SetTag("summary", "the confidential meeting content");
            });
            tx.StartChild("http.client", "POST https://hooks.example/x?token=A_WEBHOOK_SECRET").Finish();
            tx.Finish();

            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(30));
        }

        var payload = await transport.TransactionPayloadAsync();
        Assert.NotNull(payload);
        Assert.DoesNotContain("A_LIVE_JWT", payload);
        Assert.DoesNotContain("A_SESSION_COOKIE", payload);
        Assert.DoesNotContain("A_WEBHOOK_SECRET", payload);
        Assert.DoesNotContain("confidential", payload);
        Assert.DoesNotContain("dictated", payload);
        // Still useful: the route, the path and the span survive so the timing is diagnosable.
        Assert.Contains("POST /api/recordings", payload);
        Assert.Contains("https://api.example/hubs/transcription", payload);
        Assert.Contains("POST https://hooks.example/x", payload);
    }

    private sealed class CapturingTransport : ITransport
    {
        private readonly List<Envelope> _sent = [];

        public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            lock (_sent) _sent.Add(envelope);
            return Task.CompletedTask;
        }

        public async Task<string?> TransactionPayloadAsync()
        {
            Envelope[] envelopes;
            lock (_sent) envelopes = [.. _sent];
            foreach (var envelope in envelopes)
            {
                using var buffer = new MemoryStream();
                await envelope.SerializeAsync(buffer, null!);
                var text = Encoding.UTF8.GetString(buffer.ToArray());
                if (text.Contains("\"type\":\"transaction\"")) return text;
            }
            return null;
        }
    }

    // --- Cross-runtime deny-list ------------------------------------------------------------
    // The worker (src/Diariz.Worker/telemetry.py), the API (SentryScrubber) and the browser SPA
    // (apps/web/src/lib/telemetry.ts) each carry their own deny-list, and they had silently
    // diverged: the ECAPA voiceprint fix ("embedding"/"embeddings") landed only in Python, and
    // "note"/"notes" only in .NET - even though the API is the runtime that actually stores
    // voiceprints. This test pins the shared set on the .NET side;
    // tests/test_telemetry.py::test_the_shared_cross_runtime_deny_list_is_covered pins the same list
    // on the Python side, and the isSensitiveKey suite in apps/web/src/lib/telemetry.test.ts pins it
    // in the browser. Note what this does NOT catch: adding a NEW name to only one runtime is still
    // invisible, because each test only knows its own copy.

    public static TheoryData<string> SharedDenyList =>
    [
        // Exact field names carrying meeting content or biometrics.
        "text", "transcript", "transcription", "segments", "words", "summary", "minutes",
        "note", "notes", "content", "authorization", "cookie", "cookies",
        "embedding", "embeddings",
        // Substring markers for credentials.
        "secret", "token", "password", "apikey", "api_key", "accesskey", "access_key",
    ];

    [Theory]
    [MemberData(nameof(SharedDenyList))]
    public void IsSensitiveKey_CoversEveryNameInTheCrossRuntimeDenyList(string key)
    {
        Assert.True(SentryScrubber.IsSensitiveKey(key),
            $"'{key}' is on the deny-list shared with src/Diariz.Worker/telemetry.py but this runtime no longer redacts it.");
    }
}
