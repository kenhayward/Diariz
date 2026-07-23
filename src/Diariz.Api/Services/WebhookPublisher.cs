using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

public interface IWebhookPublisher
{
    /// <summary>Builds the thin envelope once and inserts one <see cref="WebhookDelivery"/> per matching active
    /// personal subscription owned by <paramref name="ownerUserId"/>. Best-effort: never throws.</summary>
    Task PublishAsync(string eventType, Guid ownerUserId, object data, CancellationToken ct = default);
}

public sealed class WebhookPublisher : IWebhookPublisher
{
    private readonly DiarizDbContext _db;
    private readonly ILogger<WebhookPublisher> _log;

    public WebhookPublisher(DiarizDbContext db, ILogger<WebhookPublisher> log) { _db = db; _log = log; }

    public async Task PublishAsync(string eventType, Guid ownerUserId, object data, CancellationToken ct = default)
    {
        try
        {
            var subs = await _db.Webhooks
                .Where(s => s.IsActive && s.Scope == WebhookScope.Personal && s.OwnerUserId == ownerUserId)
                .ToListAsync(ct);
            var matches = subs.Where(s => WebhookEventTypes.Matches(s.EventTypes, eventType)).ToList();
            if (matches.Count == 0) return;

            var eventId = "evt_" + Guid.NewGuid().ToString("N");
            var body = WebhookPayload.Build(eventId, eventType, DateTimeOffset.UtcNow, data);

            foreach (var s in matches)
            {
                _db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    Id = Guid.NewGuid(), SubscriptionId = s.Id, EventId = eventId, EventType = eventType,
                    PayloadJson = body, Status = WebhookDeliveryStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow,
                });
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Publishing must never break the originating request/worker.
            _log.LogError(ex, "Failed to enqueue webhook deliveries for {EventType}", eventType);
        }
    }
}
