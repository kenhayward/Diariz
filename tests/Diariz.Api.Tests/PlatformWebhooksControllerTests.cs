using System.Reflection;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class PlatformWebhooksControllerTests
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

    private static PlatformWebhooksController Controller(DiarizDbContext db, Guid adminId, bool urlOk) =>
        new(db, new FixedPlatformSettings(db), new PrefixProtector(), new StubUrlValidator(urlOk))
        { ControllerContext = Http.Context(adminId, [Roles.PlatformAdministrator]) };

    [Fact]
    public void Controller_IsGatedToPlatformAdministrator()
    {
        var attr = typeof(PlatformWebhooksController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("ManagePlatform", attr!.Policy);
        Assert.Null(attr.Roles);
    }

    [Fact]
    public async Task Create_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        await db.SaveChangesAsync();
        var c = Controller(db, Guid.NewGuid(), urlOk: true);
        var res = await c.Create(new CreatePlatformWebhookRequest(
            "z", "https://x/y", new[] { "recording.transcribed" }, new[] { "meeting-overrun" }));
        Assert.IsType<ForbidResult>(res.Result);
    }

    [Fact]
    public async Task Create_rejects_empty_signal_filter()
    {
        var db = Enabled(); var adminId = Guid.NewGuid();
        var c = Controller(db, adminId, urlOk: true);
        var res = await c.Create(new CreatePlatformWebhookRequest(
            "z", "https://x/y", new[] { "recording.transcribed" }, Array.Empty<string>()));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task Create_returns_secret_once_and_persists_platform_scope_and_signal_filter()
    {
        var db = Enabled(); var adminId = Guid.NewGuid();
        var c = Controller(db, adminId, urlOk: true);
        var res = await c.Create(new CreatePlatformWebhookRequest(
            "z", "https://x/y", new[] { "recording.transcribed" }, new[] { "meeting-overrun", "no-show" }));

        var dto = Assert.IsType<WebhookCreatedDto>(res.Value);
        Assert.StartsWith("dz_whsec_", dto.Secret);

        var row = await db.Webhooks.SingleAsync();
        Assert.NotEqual(dto.Secret, row.SecretEncrypted); // stored value is the protected form
        Assert.Equal("enc:" + dto.Secret, row.SecretEncrypted);
        Assert.Equal(WebhookScope.Platform, row.Scope);
        Assert.Equal(adminId, row.OwnerUserId);
        Assert.Equal("recording.transcribed", row.EventTypes);
        Assert.Equal(new[] { "meeting-overrun", "no-show" }, WebhookSignals.Split(row.SignalFilter));
    }

    [Fact]
    public async Task Create_rejects_unknown_event_type()
    {
        var db = Enabled(); var adminId = Guid.NewGuid();
        var c = Controller(db, adminId, urlOk: true);
        var res = await c.Create(new CreatePlatformWebhookRequest(
            "z", "https://x/y", new[] { "not.a.real.event" }, new[] { "meeting-overrun" }));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task Create_rejects_ssrf_url()
    {
        var db = Enabled(); var adminId = Guid.NewGuid();
        var c = Controller(db, adminId, urlOk: false);
        var res = await c.Create(new CreatePlatformWebhookRequest(
            "z", "http://169.254.169.254/", new[] { "recording.transcribed" }, new[] { "meeting-overrun" }));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task List_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        await db.SaveChangesAsync();
        var res = await Controller(db, Guid.NewGuid(), urlOk: true).List();
        Assert.IsType<ForbidResult>(res.Result);
    }

    [Fact]
    public async Task List_returns_platform_subs_with_signal_filter_and_omits_personal()
    {
        var db = Enabled(); var admin1 = Guid.NewGuid(); var admin2 = Guid.NewGuid();
        db.Webhooks.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = admin1, Scope = WebhookScope.Platform, Name = "platform-one",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            SignalFilter = "meeting-overrun",
        });
        db.Webhooks.Add(new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = admin2, Scope = WebhookScope.Personal, Name = "someones-personal",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        });
        await db.SaveChangesAsync();

        // Any admin (not just the creating one) can list all platform subscriptions.
        var res = await Controller(db, admin2, urlOk: true).List();
        var list = Assert.IsAssignableFrom<IReadOnlyList<WebhookSubscriptionDto>>(
            Assert.IsType<OkObjectResult>(res.Result).Value);

        Assert.Single(list);
        Assert.Equal("platform-one", list[0].Name);
        Assert.Equal("Platform", list[0].Scope);
        Assert.Equal(new[] { "meeting-overrun" }, list[0].SignalFilter);
    }

    [Fact]
    public async Task Update_another_admins_platform_subscription_succeeds()
    {
        var db = Enabled(); var creator = Guid.NewGuid(); var otherAdmin = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = creator, Scope = WebhookScope.Platform, Name = "z",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            SignalFilter = "meeting-overrun",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, otherAdmin, urlOk: true);
        var res = await c.Update(sub.Id, new UpdatePlatformWebhookRequest(
            "renamed", "https://x/y", new[] { "recording.transcribed" }, new[] { "no-show" }, true));

        var dto = Assert.IsType<WebhookSubscriptionDto>(res.Value);
        Assert.Equal("renamed", dto.Name);
        Assert.Equal(new[] { "no-show" }, dto.SignalFilter);
    }

    [Fact]
    public async Task Update_rejects_empty_signal_filter()
    {
        var db = Enabled(); var adminId = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = adminId, Scope = WebhookScope.Platform, Name = "z",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            SignalFilter = "meeting-overrun",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, adminId, urlOk: true);
        var res = await c.Update(sub.Id, new UpdatePlatformWebhookRequest(
            "z", "https://x/y", new[] { "recording.transcribed" }, Array.Empty<string>(), true));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Equal("meeting-overrun", (await db.Webhooks.SingleAsync()).SignalFilter); // unchanged
    }

    [Fact]
    public async Task Update_rejects_ssrf_url()
    {
        var db = Enabled(); var adminId = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = adminId, Scope = WebhookScope.Platform, Name = "z",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            SignalFilter = "meeting-overrun",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var c = Controller(db, adminId, urlOk: false);
        var res = await c.Update(sub.Id, new UpdatePlatformWebhookRequest(
            "z", "http://169.254.169.254/", new[] { "recording.transcribed" }, new[] { "meeting-overrun" }, true));
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Equal("https://x/y", (await db.Webhooks.SingleAsync()).Url); // unchanged
    }

    [Fact]
    public async Task Update_missing_subscription_returns_not_found()
    {
        var db = Enabled(); var adminId = Guid.NewGuid();
        var c = Controller(db, adminId, urlOk: true);
        var res = await c.Update(Guid.NewGuid(), new UpdatePlatformWebhookRequest(
            "z", "https://x/y", new[] { "recording.transcribed" }, new[] { "meeting-overrun" }, true));
        Assert.IsType<NotFoundResult>(res.Result);
    }

    [Fact]
    public async Task Update_ignores_a_personal_subscription_with_the_same_id()
    {
        var db = Enabled(); var adminId = Guid.NewGuid(); var owner = Guid.NewGuid();
        var personal = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, Scope = WebhookScope.Personal, Name = "personal",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
        };
        db.Webhooks.Add(personal);
        await db.SaveChangesAsync();

        var c = Controller(db, adminId, urlOk: true);
        var res = await c.Update(personal.Id, new UpdatePlatformWebhookRequest(
            "z", "https://x/y", new[] { "recording.transcribed" }, new[] { "meeting-overrun" }, true));
        Assert.IsType<NotFoundResult>(res.Result);
    }

    [Fact]
    public async Task Delete_removes_any_admins_platform_subscription()
    {
        var db = Enabled(); var creator = Guid.NewGuid(); var otherAdmin = Guid.NewGuid();
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = creator, Scope = WebhookScope.Platform, Name = "z",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            SignalFilter = "meeting-overrun",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, otherAdmin, urlOk: true).Delete(sub.Id);
        Assert.IsType<NoContentResult>(res);
        Assert.Empty(await db.Webhooks.ToListAsync());
    }

    [Fact]
    public async Task Delete_when_disabled_is_forbidden()
    {
        var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = false });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), Scope = WebhookScope.Platform, Name = "z",
            Url = "https://x/y", SecretEncrypted = "enc:s", EventTypes = "recording.transcribed",
            SignalFilter = "meeting-overrun",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();

        var res = await Controller(db, Guid.NewGuid(), urlOk: true).Delete(sub.Id);
        Assert.IsType<ForbidResult>(res);
    }
}
