using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class SummarizationSettingsResolverTests
{
    private static readonly SummarizationOptions Server = new()
    {
        ApiBase = "https://server/v1",
        ApiKey = "sk-server",
        Model = "server-model",
        TimeoutSeconds = 90,
    };

    private static SummarizationSettingsResolver Build(DiarizDbContext db) =>
        new(db, Options.Create(Server), new FakeApiKeyProtector());

    private static async Task SeedUser(DiarizDbContext db, Guid userId,
        string? apiBase, string? model, string? rawKey)
    {
        db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            SummaryApiBase = apiBase,
            SummaryModel = model,
            SummaryApiKeyEncrypted = new FakeApiKeyProtector().Protect(rawKey),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NoUserSettings_UsesServerDefaults()
    {
        using var db = TestDb.Create();
        var cfg = await Build(db).ResolveAsync(Guid.NewGuid());

        Assert.Equal("https://server/v1", cfg.ApiBase);
        Assert.Equal("sk-server", cfg.ApiKey);
        Assert.Equal("server-model", cfg.Model);
        Assert.Equal(90, cfg.TimeoutSeconds);
        Assert.True(cfg.Enabled);
    }

    [Fact]
    public async Task UserSettings_OverrideServer()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId, "https://user/v1", "user-model", "sk-user");

        var cfg = await Build(db).ResolveAsync(userId);

        Assert.Equal("https://user/v1", cfg.ApiBase);
        Assert.Equal("user-model", cfg.Model);
        Assert.Equal("sk-user", cfg.ApiKey); // decrypted from the stored ciphertext
    }

    [Fact]
    public async Task PartialUser_ModelOnly_FallsBackApiBaseAndKeyToServer()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId, apiBase: null, model: "user-model", rawKey: null);

        var cfg = await Build(db).ResolveAsync(userId);

        Assert.Equal("https://server/v1", cfg.ApiBase);
        Assert.Equal("user-model", cfg.Model);
        Assert.Equal("sk-server", cfg.ApiKey);
    }

    [Fact]
    public async Task UserConfiguredEndpoint_WhenServerEmpty_IsEnabled()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId, "https://user/v1", "m", "sk-user");
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions { ApiBase = "" }), new FakeApiKeyProtector());

        var cfg = await resolver.ResolveAsync(userId);

        Assert.Equal("https://user/v1", cfg.ApiBase);
        Assert.True(cfg.Enabled);
    }

    [Fact]
    public async Task NeitherConfigured_IsDisabled()
    {
        using var db = TestDb.Create();
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions { ApiBase = "" }), new FakeApiKeyProtector());

        Assert.False((await resolver.ResolveAsync(Guid.NewGuid())).Enabled);
    }
}
