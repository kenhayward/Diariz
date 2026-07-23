using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class WebhooksControllerTests
{
    /// <summary>Prefixing stand-in for the Data Protection protector - lets the "encrypted at rest" test
    /// assert the stored value differs from the plaintext secret without a real key ring.</summary>
    private sealed class PrefixProtector : IWebhookSecretProtector
    {
        public string? Protect(string? plaintext) => plaintext is null ? null : "enc:" + plaintext;
        public string? Unprotect(string? ciphertext) =>
            ciphertext is null ? null : ciphertext.StartsWith("enc:") ? ciphertext["enc:".Length..] : ciphertext;
    }

    /// <summary>Canned <see cref="IWebhookUrlValidator"/> - Valid or Invalid per the test's <c>urlOk</c> flag.</summary>
    private sealed class StubUrlValidator(bool ok) : IWebhookUrlValidator
    {
        public Task<WebhookUrlValidation> ValidateAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(ok ? WebhookUrlValidation.Valid : WebhookUrlValidation.Invalid("That address is not allowed."));
    }

    private static DiarizDbContext Enabled()
    {
        var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = true });
        db.SaveChanges();
        return db;
    }

    private static WebhooksController Controller(DiarizDbContext db, Guid userId, bool urlOk) =>
        new(db, new FixedPlatformSettings(db), new PrefixProtector(), new StubUrlValidator(urlOk))
        { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task Create_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        await db.SaveChangesAsync();
        var c = Controller(db, Guid.NewGuid(), urlOk: true);
        var res = await c.Create(new CreateWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }));
        Assert.IsType<ForbidResult>(res.Result);
    }

    [Fact]
    public async Task Create_returns_secret_once_and_persists_encrypted()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var c = Controller(db, userId, urlOk: true);
        var res = await c.Create(new CreateWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }));
        var dto = Assert.IsType<WebhookCreatedDto>(res.Value);
        Assert.StartsWith("dz_whsec_", dto.Secret);
        var row = await db.Webhooks.SingleAsync();
        Assert.NotEqual(dto.Secret, row.SecretEncrypted); // stored value is the protected form
        Assert.Equal("enc:" + dto.Secret, row.SecretEncrypted);
        Assert.Equal("recording.transcribed", row.EventTypes);
    }

    [Fact]
    public async Task Create_rejects_unknown_event_type()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var c = Controller(db, userId, urlOk: true);
        var res = await c.Create(new CreateWebhookRequest("z", "https://x/y", new[] { "not.a.real.event" }));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task Create_rejects_ssrf_url()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var c = Controller(db, userId, urlOk: false);
        var res = await c.Create(new CreateWebhookRequest("z", "http://169.254.169.254/", new[] { "recording.transcribed" }));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task Test_endpoint_enqueues_a_ping_delivery()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, userId, urlOk: true);
        await c.SendTest(sub.Id);
        var d = await db.WebhookDeliveries.SingleAsync();
        Assert.Equal(WebhookEventTypes.Ping, d.EventType);
        Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);
    }

    [Fact]
    public async Task List_omits_secret_and_scopes_to_owner()
    {
        var db = Enabled(); var userId = Guid.NewGuid(); var other = Guid.NewGuid();
        db.Webhooks.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "mine", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        });
        db.Webhooks.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = other, Name = "theirs", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        });
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true).List();
        var list = Assert.IsAssignableFrom<IReadOnlyList<WebhookSubscriptionDto>>(
            Assert.IsType<OkObjectResult>(res.Result).Value);
        Assert.Single(list);
        Assert.Equal("mine", list[0].Name);
    }

    [Fact]
    public async Task Create_rejects_over_the_per_user_cap()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        for (var i = 0; i < WebhooksController.MaxPerUser; i++)
        {
            db.Webhooks.Add(new WebhookSubscription
            {
                Id = Guid.NewGuid(), OwnerUserId = userId, Name = $"s{i}", Url = "https://x/y",
                SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            });
        }
        await db.SaveChangesAsync();

        var c = Controller(db, userId, urlOk: true);
        var res = await c.Create(new CreateWebhookRequest("one-too-many", "https://x/y", new[] { "recording.transcribed" }));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Equal(WebhooksController.MaxPerUser, await db.Webhooks.CountAsync());
    }

    [Fact]
    public async Task Update_others_subscription_returns_not_found()
    {
        var db = Enabled(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, other, urlOk: true);
        var res = await c.Update(sub.Id, new UpdateWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }, true));
        Assert.IsType<NotFoundResult>(res.Result);
    }

    [Fact]
    public async Task Update_reactivating_an_auto_disabled_subscription_resets_failure_state()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            IsActive = false, ConsecutiveFailures = 10, DisabledReason = "Too many consecutive failures.",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, userId, urlOk: true);
        var res = await c.Update(sub.Id, new UpdateWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }, true));

        var dto = Assert.IsType<WebhookSubscriptionDto>(res.Value);
        Assert.True(dto.IsActive);
        Assert.Equal(0, dto.ConsecutiveFailures);
        Assert.Null(dto.DisabledReason);
    }

    [Fact]
    public async Task Delete_removes_owned_subscription()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true).Delete(sub.Id);
        Assert.IsType<NoContentResult>(res);
        Assert.Empty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task Deliveries_returns_owned_subscriptions_deliveries_only()
    {
        var db = Enabled(); var userId = Guid.NewGuid(); var other = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        var othersSub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = other, Name = "theirs", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.AddRange(sub, othersSub);
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = sub.Id, EventId = "evt_1",
            EventType = WebhookEventTypes.RecordingTranscribed, PayloadJson = "{}",
            Status = WebhookDeliveryStatus.Delivered, NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true).Deliveries(sub.Id);
        var list = Assert.IsAssignableFrom<IReadOnlyList<WebhookDeliveryDto>>(
            Assert.IsType<OkObjectResult>(res.Result).Value);
        Assert.Single(list);
        Assert.Equal(WebhookEventTypes.RecordingTranscribed, list[0].EventType);
    }

    [Fact]
    public async Task Deliveries_for_others_subscription_returns_not_found()
    {
        var db = Enabled(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, other, urlOk: true).Deliveries(sub.Id);
        Assert.IsType<NotFoundResult>(res.Result);
    }

    [Fact]
    public async Task Delete_others_subscription_returns_not_found()
    {
        var db = Enabled(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, other, urlOk: true).Delete(sub.Id);
        Assert.IsType<NotFoundResult>(res);
        Assert.NotEmpty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task SendTest_others_subscription_returns_not_found()
    {
        var db = Enabled(); var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, other, urlOk: true).SendTest(sub.Id);
        Assert.IsType<NotFoundResult>(res);
        Assert.Empty(await db.WebhookDeliveries.ToListAsync());
    }

    [Fact]
    public async Task Update_rejects_ssrf_url()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, userId, urlOk: false);
        var res = await c.Update(sub.Id, new UpdateWebhookRequest("z", "http://169.254.169.254/", new[] { "recording.transcribed" }, true));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Equal("https://x/y", (await db.Webhooks.SingleAsync()).Url); // unchanged
    }

    [Fact]
    public async Task Update_rejects_unknown_event_type()
    {
        var db = Enabled(); var userId = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, userId, urlOk: true);
        var res = await c.Update(sub.Id, new UpdateWebhookRequest("z", "https://x/y", new[] { "not.a.real.event" }, true));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Equal("recording.transcribed", (await db.Webhooks.SingleAsync()).EventTypes); // unchanged
    }

    [Fact]
    public async Task List_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        db.Webhooks.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        });
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true).List();
        Assert.IsType<ForbidResult>(res.Result);
    }

    [Fact]
    public async Task Update_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true)
            .Update(sub.Id, new UpdateWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }, true));
        Assert.IsType<ForbidResult>(res.Result);
    }

    [Fact]
    public async Task Delete_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true).Delete(sub.Id);
        Assert.IsType<ForbidResult>(res);
    }

    [Fact]
    public async Task SendTest_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true).SendTest(sub.Id);
        Assert.IsType<ForbidResult>(res);
    }

    [Fact]
    public async Task Deliveries_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Name = "z", Url = "https://x/y",
            SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, userId, urlOk: true).Deliveries(sub.Id);
        Assert.IsType<ForbidResult>(res.Result);
    }
}
