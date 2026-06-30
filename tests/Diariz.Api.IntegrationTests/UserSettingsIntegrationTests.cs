using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class UserSettingsIntegrationTests(ContainersFixture fx)
{
    // Real Data Protection (in-process keyring) — exercises actual encryption, not the fake prefix.
    private static readonly IApiKeyProtector Protector = new ApiKeyProtector(new EphemeralDataProtectionProvider());

    private static UserSettingsController Settings(Diariz.Domain.DiarizDbContext db, Guid userId)
    {
        var chat = new ChatOptions();
        var resolver = new ChatToolSettingsResolver(
            db, new Diariz.Api.Tools.ChatToolRegistry([]), Options.Create(chat));
        return new(db, Protector, Options.Create(new SummarizationOptions()), Options.Create(chat), resolver)
        { ControllerContext = Http.Context(userId) };
    }

    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Settings_PersistAcrossContexts_AndKeyIsEncryptedAtRest()
    {
        var userId = await SeedUser();

        await using (var db = fx.CreateDbContext())
            await Settings(db, userId).Update(new UpdateUserSettingsRequest("https://mine/v1", "my-model", "sk-real-secret"));

        await using (var verify = fx.CreateDbContext())
        {
            var stored = await verify.UserSettings.SingleAsync(s => s.UserId == userId);
            Assert.Equal("https://mine/v1", stored.SummaryApiBase);
            Assert.Equal("my-model", stored.SummaryModel);
            Assert.NotNull(stored.SummaryApiKeyEncrypted);
            Assert.DoesNotContain("sk-real-secret", stored.SummaryApiKeyEncrypted!); // real ciphertext
        }
    }

    [Fact]
    public async Task Resolver_DecryptsStoredKey_RoundTrip()
    {
        var userId = await SeedUser();
        await using (var db = fx.CreateDbContext())
            await Settings(db, userId).Update(new UpdateUserSettingsRequest("https://mine/v1", "my-model", "sk-real-secret"));

        await using var ctx = fx.CreateDbContext();
        var resolver = new SummarizationSettingsResolver(
            ctx, Options.Create(new SummarizationOptions { ApiBase = "https://server", ApiKey = "sk-server", Model = "srv" }), Protector);

        var cfg = await resolver.ResolveAsync(userId);

        Assert.Equal("https://mine/v1", cfg.ApiBase);
        Assert.Equal("my-model", cfg.Model);
        Assert.Equal("sk-real-secret", cfg.ApiKey); // decrypted back to the original
    }

    [Fact]
    public async Task DeletingUser_CascadesSettings()
    {
        var userId = await SeedUser();
        await using (var db = fx.CreateDbContext())
            await Settings(db, userId).Update(new UpdateUserSettingsRequest("https://mine/v1", "m", "sk"));

        await using (var db = fx.CreateDbContext())
        {
            var user = await db.Users.FindAsync(userId);
            db.Users.Remove(user!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.UserSettings.AnyAsync(s => s.UserId == userId));
    }
}
