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

    [Fact]
    public async Task Platform_sub_matches_by_signal_and_gets_inline_output_for_formula_events()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        // A personal sub owned by someone else, and a platform sub routed on "post-to-slack".
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Personal,
            Name = "p", Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed", IsActive = true });
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform,
            Name = "plat", Url = "https://x/z", SecretEncrypted = "c", EventTypes = "formula_result.completed",
            SignalFilter = "post-to-slack", IsActive = true });
        // An empty-filter platform sub must NOT match.
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform,
            Name = "plat-empty", Url = "https://x/w", SecretEncrypted = "c", EventTypes = "formula_result.completed",
            SignalFilter = null, IsActive = true });
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync("formula_result.completed", owner,
            data: new { formulaResultId = Guid.NewGuid(), status = "Ready" },
            signals: new[] { "post-to-slack" },
            platformData: new { formulaResultId = Guid.NewGuid(), status = "Ready", output = "the output", formulaName = "F" });

        var deliveries = await db.WebhookDeliveries.Include(d => d.Subscription).ToListAsync();
        // Only the signal-matched platform sub fires (owner has no personal sub; other personal belongs to someone else).
        Assert.Single(deliveries);
        Assert.Equal(WebhookScope.Platform, deliveries[0].Subscription!.Scope);
        Assert.Contains("\"output\":\"the output\"", deliveries[0].PayloadJson); // inline output embedded
    }

    [Fact]
    public async Task Personal_delivery_stays_thin_even_when_platform_data_is_supplied()
    {
        var db = TestDb.Create();
        var owner = Guid.NewGuid();
        db.Webhooks.Add(new WebhookSubscription { Id = Guid.NewGuid(), OwnerUserId = owner, Scope = WebhookScope.Personal,
            Name = "mine", Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed", IsActive = true });
        await db.SaveChangesAsync();

        var pub = new WebhookPublisher(db, NullLogger<WebhookPublisher>.Instance);
        await pub.PublishAsync("formula_result.completed", owner,
            data: new { formulaResultId = Guid.NewGuid(), status = "Ready" },
            signals: new[] { "post-to-slack" },
            platformData: new { output = "secret output" });

        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.DoesNotContain("output", d.PayloadJson); // personal never embeds output
    }
}
