using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

public interface IWebhookPublisher
{
    /// <summary>Enqueues one <see cref="WebhookDelivery"/> per matching subscription. Personal subs (owned by
    /// <paramref name="ownerUserId"/>) get the thin <paramref name="data"/> body; platform subs whose SignalFilter
    /// intersects <paramref name="signals"/> get <paramref name="platformData"/> (the inline-output body) when
    /// supplied, else the thin body. Best-effort: never throws.</summary>
    Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default);
}

public sealed class WebhookPublisher : IWebhookPublisher
{
    private readonly DiarizDbContext _db;
    private readonly ILogger<WebhookPublisher> _log;

    public WebhookPublisher(DiarizDbContext db, ILogger<WebhookPublisher> log) { _db = db; _log = log; }

    public async Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, CancellationToken ct = default)
    {
        try
        {
            var sig = signals ?? Array.Empty<string>();
            var all = await _db.Webhooks
                .Where(s => s.IsActive
                    && ((s.Scope == WebhookScope.Personal && s.OwnerUserId == ownerUserId)
                        || s.Scope == WebhookScope.Platform))
                .ToListAsync(ct);

            var personal = all.Where(s => s.Scope == WebhookScope.Personal
                && WebhookEventTypes.Matches(s.EventTypes, eventType)
                && (WebhookSignals.IsEmpty(s.SignalFilter) || WebhookSignals.Intersects(s.SignalFilter, sig))).ToList();
            var platform = all.Where(s => s.Scope == WebhookScope.Platform
                && WebhookEventTypes.Matches(s.EventTypes, eventType)
                && WebhookSignals.Intersects(s.SignalFilter, sig)).ToList();   // empty filter -> Intersects false -> no match

            if (personal.Count == 0 && platform.Count == 0) return;

            var eventId = "evt_" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var thinBody = WebhookPayload.Build(eventId, eventType, now, data);
            var platformBody = platformData is null ? thinBody : WebhookPayload.Build(eventId, eventType, now, platformData);

            foreach (var s in personal) Enqueue(s, eventId, eventType, thinBody, now);
            foreach (var s in platform) Enqueue(s, eventId, eventType, platformBody, now);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Publishing must never break the originating request/worker.
            _log.LogError(ex, "Failed to enqueue webhook deliveries for {EventType}", eventType);
        }
    }

    private void Enqueue(WebhookSubscription s, string eventId, string eventType, string body, DateTimeOffset now) =>
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = s.Id, EventId = eventId, EventType = eventType,
            PayloadJson = body, Status = WebhookDeliveryStatus.Pending, NextAttemptAt = now,
        });
}
