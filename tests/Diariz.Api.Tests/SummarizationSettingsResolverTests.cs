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

    // ---- Timeout (platform-wide, admin-set; falls back to the server option) ----

    [Fact]
    public async Task Timeout_UsesPlatformSetting_WhenPresent()
    {
        using var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 300 });
        await db.SaveChangesAsync();

        Assert.Equal(300, (await Build(db).ResolveAsync(Guid.NewGuid())).TimeoutSeconds);
    }

    [Fact]
    public async Task Timeout_FallsBackToServerOption_WhenNoPlatformRow()
    {
        using var db = TestDb.Create();
        Assert.Equal(90, (await Build(db).ResolveAsync(Guid.NewGuid())).TimeoutSeconds); // Server.TimeoutSeconds
    }

    // ---- Reasoning (effective effort: null = omit the field) ----

    [Fact]
    public async Task Reasoning_OffByDefault_NoEffort()
    {
        using var db = TestDb.Create();
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions { ApiBase = "https://s/v1" }), new FakeApiKeyProtector());

        Assert.Null((await resolver.ResolveAsync(Guid.NewGuid())).ReasoningEffort);
    }

    [Fact]
    public async Task Reasoning_ServerEnabled_UsesServerEffort()
    {
        using var db = TestDb.Create();
        var resolver = new SummarizationSettingsResolver(
            db,
            Options.Create(new SummarizationOptions
            {
                ApiBase = "https://s/v1", ReasoningEnabled = true, ReasoningEffort = "high",
            }),
            new FakeApiKeyProtector());

        Assert.Equal("high", (await resolver.ResolveAsync(Guid.NewGuid())).ReasoningEffort);
    }

    [Fact]
    public async Task Reasoning_UserEffort_OverridesServer()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = userId, ReasoningEnabled = true, ReasoningEffort = "low" });
        await db.SaveChangesAsync();

        var resolver = new SummarizationSettingsResolver(
            db,
            Options.Create(new SummarizationOptions
            {
                ApiBase = "https://s/v1", ReasoningEnabled = false, ReasoningEffort = "high",
            }),
            new FakeApiKeyProtector());

        Assert.Equal("low", (await resolver.ResolveAsync(userId)).ReasoningEffort);
    }

    [Fact]
    public async Task Reasoning_UserDisables_OverridesServerEnabled()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = userId, ReasoningEnabled = false });
        await db.SaveChangesAsync();

        var resolver = new SummarizationSettingsResolver(
            db,
            Options.Create(new SummarizationOptions
            {
                ApiBase = "https://s/v1", ReasoningEnabled = true, ReasoningEffort = "high",
            }),
            new FakeApiKeyProtector());

        Assert.Null((await resolver.ResolveAsync(userId)).ReasoningEffort);
    }
}
