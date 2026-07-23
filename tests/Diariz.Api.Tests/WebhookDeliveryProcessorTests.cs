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
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }
        public StubHandler(HttpStatusCode status) => _status = status;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status);
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

    private static WebhookDeliveryProcessor Processor() =>
        new(new PlainProtector(), Options.Create(new WebhookOptions()), NullLogger<WebhookDeliveryProcessor>.Instance);

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
}
