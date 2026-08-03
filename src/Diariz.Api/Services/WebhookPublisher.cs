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
    /// supplied, else the thin body. Best-effort: never throws.
    ///
    /// <para><paramref name="dataWithContacts"/> is the same body but carrying attendees' email addresses and
    /// phone numbers. A subscription only receives it when its owner has ticked
    /// <see cref="WebhookSubscription.IncludeAttendeeContacts"/> - without that, an automation pointed at any
    /// URL would fan the directory's contact details out to it. Built lazily, so the ordinary case where
    /// nobody has opted in costs nothing.</para>
    ///
    /// <para><paramref name="dataWithFeedbackText"/> is the same body but carrying the submitter's free-text
    /// description, for <c>feedback.submitted</c>. A subscription only receives it when its owner has ticked
    /// <see cref="WebhookSubscription.IncludeFeedbackText"/> - the same reasoning as the contacts gate: an
    /// automation points at an arbitrary URL, and feedback text may quote meeting content. Built lazily, so
    /// the ordinary case where nobody has opted in costs nothing.</para></summary>
    Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, object? dataWithContacts = null,
        object? dataWithFeedbackText = null, CancellationToken ct = default);
}

public sealed class WebhookPublisher : IWebhookPublisher
{
    private readonly DiarizDbContext _db;
    private readonly ILogger<WebhookPublisher> _log;

    public WebhookPublisher(DiarizDbContext db, ILogger<WebhookPublisher> log) { _db = db; _log = log; }

    public async Task PublishAsync(string eventType, Guid ownerUserId, object data,
        IReadOnlyList<string>? signals = null, object? platformData = null, object? dataWithContacts = null,
        object? dataWithFeedbackText = null, CancellationToken ct = default)
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

            // Only serialized when something actually asked for contacts - the default is nobody.
            string? contactsBody = null;
            // Only serialized when something actually asked for the feedback text - the default is nobody.
            string? feedbackBody = null;
            string BodyFor(WebhookSubscription s, string standard)
            {
                if (s.IncludeFeedbackText && dataWithFeedbackText is not null)
                    return feedbackBody ??= WebhookPayload.Build(eventId, eventType, now, dataWithFeedbackText);
                if (!s.IncludeAttendeeContacts || dataWithContacts is null) return standard;
                return contactsBody ??= WebhookPayload.Build(eventId, eventType, now, dataWithContacts);
            }

            foreach (var s in personal) Enqueue(s, eventId, eventType, BodyFor(s, thinBody), now);
            foreach (var s in platform) Enqueue(s, eventId, eventType, BodyFor(s, platformBody), now);
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
