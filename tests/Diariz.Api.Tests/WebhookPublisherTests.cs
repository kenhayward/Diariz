using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class WebhookPublisherTests
{
    private static WebhookSubscription Sub(Guid owner, string events, bool active = true) => new()
    {
        Id = Guid.NewGuid(), OwnerUserId = owner, Name = "s", Url = "https://x/y",
        SecretEncrypted = "c", EventTypes = events, IsActive = active,
    };

    [Fact]
    public async Task Publishes_one_delivery_per_matching_active_subscription()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.Webhooks.Add(Sub(owner, "recording.transcribed,formula_result.completed"));   // matches
        db.Webhooks.Add(Sub(owner, "recording.created"));                                 // wrong type
        db.Webhooks.Add(Sub(owner, "recording.transcribed", active: false));             // inactive
        db.Webhooks.Add(Sub(other, "recording.transcribed"));                            // wrong owner
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingTranscribed, owner,
            new { recordingId = Guid.NewGuid(), status = "Transcribed" });

        var deliveries = await db.WebhookDeliveries.ToListAsync();
        Assert.Single(deliveries);
        Assert.Equal(WebhookEventTypes.RecordingTranscribed, deliveries[0].EventType);
        Assert.Equal(WebhookDeliveryStatus.Pending, deliveries[0].Status);
        Assert.Contains("\"type\":\"recording.transcribed\"", deliveries[0].PayloadJson);
        Assert.False(string.IsNullOrEmpty(deliveries[0].EventId));
    }

    [Fact]
    public async Task No_subscribers_inserts_nothing()
    {
        var db = TestDb.Create();
        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync(WebhookEventTypes.RecordingCreated, Guid.NewGuid(), new { });
        Assert.Empty(await db.WebhookDeliveries.ToListAsync());
    }
}
