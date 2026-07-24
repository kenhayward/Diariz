using System.Net;
using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class WebhookDeliveryProcessorTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly TimeSpan? _retryAfter;
        public int Sends { get; private set; }
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }
        public StubHandler(HttpStatusCode status, TimeSpan? retryAfter = null) { _status = status; _retryAfter = retryAfter; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Sends++;
            Last = req;
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            var resp = new HttpResponseMessage(_status);
            if (_retryAfter is { } ra)
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
            return resp;
        }
    }

    private sealed class PlainProtector : IWebhookSecretProtector
    {
        public string? Protect(string? p) => p;
        public string? Unprotect(string? c) => c; // secret stored as plaintext in these tests
    }

    private static (DiarizDbContext db, WebhookSubscription sub, WebhookDelivery del) Seed(string body = "{\"a\":1}")
    {
        var db = TestDb.Create();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Name = "s", Url = "https://sink.example.com/hook",
            SecretEncrypted = "shh", EventTypes = "recording.transcribed", IsActive = true,
        };
        var del = new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = "evt_1", EventType = "recording.transcribed",
            PayloadJson = body, Status = WebhookDeliveryStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        db.Webhooks.Add(sub); db.WebhookDeliveries.Add(del); db.SaveChanges();
        return (db, sub, del);
    }

    private static WebhookDeliveryProcessor Processor() => Processor(new WebhookOptions());

    private static WebhookDeliveryProcessor Processor(WebhookOptions opts) =>
        new(new PlainProtector(), Options.Create(opts), NullLogger<WebhookDeliveryProcessor>.Instance);

    private static HttpClient Client(StubHandler h) => new(h);

    [Fact]
    public async Task Success_marks_delivered_signs_and_resets_failures()
    {
        var (db, sub, del) = Seed();
        var h = new StubHandler(HttpStatusCode.OK);
        await Processor().ProcessDueAsync(db, Client(h), DateTimeOffset.UtcNow, default);

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookDeliveryStatus.Delivered, d.Status);
        Assert.Equal(200, d.ResponseStatus);
        Assert.Equal("evt_1", h.Last!.Headers.GetValues("webhook-id").Single());
        Assert.True(h.Last.Headers.Contains("webhook-timestamp"));
        var sig = h.Last.Headers.GetValues("webhook-signature").Single();
        var ts = long.Parse(h.Last.Headers.GetValues("webhook-timestamp").Single());
        Assert.Equal(WebhookSigner.Sign("shh", "evt_1", ts, "{\"a\":1}"), sig);
        Assert.Equal(0, (await db.Webhooks.SingleAsync()).ConsecutiveFailures);
        Assert.Equal("{\"a\":1}", h.LastBody);
    }

    [Fact]
    public async Task Failure_schedules_a_retry_and_increments_attempts()
    {
        var (db, _, _) = Seed();
        await Processor().ProcessDueAsync(db, Client(new StubHandler(HttpStatusCode.InternalServerError)),
            DateTimeOffset.UtcNow, default);

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);
        Assert.Equal(1, d.AttemptCount);
        Assert.True(d.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Equal(500, d.ResponseStatus);
    }

    [Fact]
    public async Task Final_failure_marks_failed()
    {
        var (db, _, del) = Seed();
        del.AttemptCount = WebhookBackoff.MaxAttempts - 1; // this attempt is the last
        await db.SaveChangesAsync();
        await Processor().ProcessDueAsync(db, Client(new StubHandler(HttpStatusCode.InternalServerError)),
            DateTimeOffset.UtcNow, default);

        Assert.Equal(WebhookDeliveryStatus.Failed, (await db.WebhookDeliveries.SingleAsync()).Status);
        Assert.Equal(1, (await db.Webhooks.SingleAsync()).ConsecutiveFailures);
    }

    [Fact]
    public async Task Auto_disables_after_threshold_consecutive_failures()
    {
        var (db, sub, del) = Seed();
        sub.ConsecutiveFailures = new WebhookOptions().AutoDisableThreshold - 1;
        del.AttemptCount = WebhookBackoff.MaxAttempts - 1;
        await db.SaveChangesAsync();
        await Processor().ProcessDueAsync(db, Client(new StubHandler(HttpStatusCode.BadGateway)),
            DateTimeOffset.UtcNow, default);

        var s = await db.Webhooks.SingleAsync();
        Assert.False(s.IsActive);
        Assert.NotNull(s.DisabledReason);
    }

    private sealed class ShutdownCancelHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource _cts;
        public ShutdownCancelHandler(CancellationTokenSource cts) => _cts = cts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            _cts.Cancel();
            throw new OperationCanceledException(_cts.Token);
        }
    }

    [Fact]
    public async Task Shutdown_cancellation_is_not_counted_as_a_failure()
    {
        var (db, sub, del) = Seed();
        using var cts = new CancellationTokenSource(); // not pre-cancelled: the initial due-deliveries query must run
        using var http = new HttpClient(new ShutdownCancelHandler(cts));

        await Processor().ProcessDueAsync(db, http, DateTimeOffset.UtcNow, cts.Token);

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);
        Assert.Equal(0, d.AttemptCount);
        Assert.NotEqual(WebhookDeliveryStatus.Failed, d.Status);
        Assert.Equal(0, (await db.Webhooks.SingleAsync()).ConsecutiveFailures);
    }

    [Fact]
    public async Task Retry_after_429_reschedules_and_is_not_a_failure()
    {
        var (db, _, _) = Seed();
        var now = DateTimeOffset.UtcNow;
        var h = new StubHandler(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(30));
        await Processor().ProcessDueAsync(db, Client(h), now, default);

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);   // not Failed
        Assert.Equal(429, d.ResponseStatus);
        Assert.Equal(0, d.AttemptCount);                         // a 429 does not consume an attempt
        Assert.True(d.NextAttemptAt >= now.AddSeconds(29) && d.NextAttemptAt <= now.AddSeconds(31));
        Assert.Equal(0, (await db.Webhooks.SingleAsync()).ConsecutiveFailures); // not a failure toward auto-disable
    }

    [Fact]
    public async Task Retry_after_429_without_header_uses_fallback()
    {
        var (db, _, _) = Seed();
        var now = DateTimeOffset.UtcNow;
        var h = new StubHandler(HttpStatusCode.TooManyRequests); // no Retry-After header
        await Processor(new WebhookOptions { RetryAfterFallbackSeconds = 90 }).ProcessDueAsync(db, Client(h), now, default);

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);
        Assert.Equal(0, d.AttemptCount);
        Assert.True(d.NextAttemptAt >= now.AddSeconds(89) && d.NextAttemptAt <= now.AddSeconds(91));
    }

    private static (DiarizDbContext db, WebhookSubscription sub) SeedSubWithDeliveries(int count, DateTimeOffset now)
    {
        var db = TestDb.Create();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Name = "s", Url = "https://sink.example.com/hook",
            SecretEncrypted = "shh", EventTypes = "recording.transcribed", IsActive = true,
        };
        db.Webhooks.Add(sub);
        for (var i = 0; i < count; i++)
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = $"evt_{i}", EventType = "recording.transcribed",
                PayloadJson = "{}", Status = WebhookDeliveryStatus.Pending, NextAttemptAt = now.AddMinutes(-1),
            });
        db.SaveChanges();
        return (db, sub);
    }

    [Fact]
    public async Task Pace_cap_defers_excess_deliveries_for_one_subscription()
    {
        var now = DateTimeOffset.UtcNow;
        var (db, _) = SeedSubWithDeliveries(4, now);
        var h = new StubHandler(HttpStatusCode.OK);

        await Processor(new WebhookOptions { MaxPerSubscriptionPerMinute = 2 }).ProcessDueAsync(db, Client(h), now, default);

        Assert.Equal(2, h.Sends); // only the cap was actually dispatched this pass
        Assert.Equal(2, await db.WebhookDeliveries.CountAsync(d => d.Status == WebhookDeliveryStatus.Delivered));
        var deferred = await db.WebhookDeliveries.Where(d => d.Status == WebhookDeliveryStatus.Pending).ToListAsync();
        Assert.Equal(2, deferred.Count);
        Assert.All(deferred, d =>
        {
            Assert.True(d.NextAttemptAt > now); // deferred, not dropped
            Assert.Equal(0, d.AttemptCount);    // a paced delivery consumes no attempt
            Assert.Null(d.LastAttemptAt);       // it was never contacted
        });
    }

    [Fact]
    public async Task Pace_cap_counts_recent_attempts_in_the_rolling_window()
    {
        var now = DateTimeOffset.UtcNow;
        var db = TestDb.Create();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Name = "s", Url = "https://sink.example.com/hook",
            SecretEncrypted = "shh", EventTypes = "recording.transcribed", IsActive = true,
        };
        db.Webhooks.Add(sub);
        // Two deliveries already attempted 10s ago - inside the one-minute window, so the cap is already reached.
        for (var i = 0; i < 2; i++)
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = $"old_{i}", EventType = "recording.transcribed",
                PayloadJson = "{}", Status = WebhookDeliveryStatus.Delivered, NextAttemptAt = now.AddMinutes(-5),
                LastAttemptAt = now.AddSeconds(-10),
            });
        var fresh = new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = "fresh", EventType = "recording.transcribed",
            PayloadJson = "{}", Status = WebhookDeliveryStatus.Pending, NextAttemptAt = now.AddMinutes(-1),
        };
        db.WebhookDeliveries.Add(fresh);
        await db.SaveChangesAsync();

        var h = new StubHandler(HttpStatusCode.OK);
        await Processor(new WebhookOptions { MaxPerSubscriptionPerMinute = 2 }).ProcessDueAsync(db, Client(h), now, default);

        Assert.Equal(0, h.Sends); // window already at the cap, so the fresh delivery is deferred, not sent
        var d = await db.WebhookDeliveries.SingleAsync(x => x.Id == fresh.Id);
        Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);
        Assert.True(d.NextAttemptAt > now);
    }
}
