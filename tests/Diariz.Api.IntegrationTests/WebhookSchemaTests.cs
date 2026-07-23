using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class WebhookSchemaTests(ContainersFixture fx)
{
    [Fact]
    public async Task Subscription_and_delivery_round_trip_and_cascade_on_user_delete()
    {
        await using var db = fx.CreateDbContext();
        var userId = await SeedUser(db);

        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "Zap", Url = "https://hooks.example.com/x",
            SecretEncrypted = "cipher", EventTypes = "recording.transcribed,formula_result.completed",
        };
        db.Webhooks.Add(sub);
        var deliveryId = Guid.NewGuid();
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = deliveryId, SubscriptionId = sub.Id, EventId = "evt_1", EventType = "recording.transcribed",
            PayloadJson = "{\"id\":\"evt_1\"}", NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var reloaded = await db.Webhooks.SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(WebhookScope.Personal, reloaded.Scope);
        Assert.True(reloaded.IsActive);
        Assert.Equal(WebhookDeliveryStatus.Pending,
            (await db.WebhookDeliveries.SingleAsync(d => d.SubscriptionId == sub.Id)).Status);

        // Deleting the owning user cascades the subscription and its deliveries.
        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == userId));
        await db.SaveChangesAsync();
        Assert.False(await db.Webhooks.AnyAsync(s => s.Id == sub.Id));
        Assert.False(await db.WebhookDeliveries.AnyAsync(d => d.Id == deliveryId));
    }

    private static async Task<Guid> SeedUser(DiarizDbContext db)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, Email = $"{id:N}@e.com", UserName = $"{id:N}@e.com" });
        await db.SaveChangesAsync();
        return id;
    }
}
