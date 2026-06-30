using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class UserSettingsControllerTests
{
    private static UserSettingsController Build(
        DiarizDbContext db, Guid userId, SummarizationOptions? server = null, ChatOptions? chat = null,
        IEnumerable<Diariz.Api.Tools.IChatTool>? tools = null)
    {
        var chatOpts = chat ?? new ChatOptions();
        var registry = new Diariz.Api.Tools.ChatToolRegistry(tools ?? []);
        var toolResolver = new ChatToolSettingsResolver(db, registry, Options.Create(chatOpts));
        return new(db, new FakeApiKeyProtector(), Options.Create(server ?? new SummarizationOptions()),
            Options.Create(chatOpts), toolResolver)
        {
            ControllerContext = Http.Context(userId),
        };
    }

    [Fact]
    public async Task Get_NoSettings_ReturnsEmpty()
    {
        using var db = TestDb.Create();
        var dto = await Build(db, Guid.NewGuid()).Get();

        Assert.Null(dto.ApiBase);
        Assert.Null(dto.Model);
        Assert.False(dto.HasApiKey);
    }

    [Fact]
    public async Task Put_CreatesSettings_AndGetReflectsThem_WithoutLeakingKey()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        var put = await Build(db, userId).Update(
            new UpdateUserSettingsRequest("https://llm.test/v1", "gpt-x", "sk-secret"));
        Assert.IsType<NoContentResult>(put);

        var dto = await Build(db, userId).Get();
        Assert.Equal("https://llm.test/v1", dto.ApiBase);
        Assert.Equal("gpt-x", dto.Model);
        Assert.True(dto.HasApiKey);

        // The key is stored via the protector (not raw), and the DTO must not expose it at all.
        // Real-encryption-at-rest is asserted in the integration harness with actual Data Protection.
        var stored = await db.UserSettings.SingleAsync(s => s.UserId == userId);
        Assert.NotNull(stored.SummaryApiKeyEncrypted);
        Assert.NotEqual("sk-secret", stored.SummaryApiKeyEncrypted);
        Assert.DoesNotContain("sk-secret", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public async Task Put_NullApiKey_LeavesExistingKeyUnchanged()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await Build(db, userId).Update(new UpdateUserSettingsRequest("https://a", "m", "sk-keep"));

        await Build(db, userId).Update(new UpdateUserSettingsRequest("https://b", "m2", ApiKey: null));

        var dto = await Build(db, userId).Get();
        Assert.Equal("https://b", dto.ApiBase);
        Assert.True(dto.HasApiKey); // key preserved
    }

    [Fact]
    public async Task Put_EmptyApiKey_ClearsKey()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await Build(db, userId).Update(new UpdateUserSettingsRequest("https://a", "m", "sk-clear-me"));

        await Build(db, userId).Update(new UpdateUserSettingsRequest("https://a", "m", ApiKey: ""));

        Assert.False((await Build(db, userId).Get()).HasApiKey);
    }

    [Fact]
    public async Task Put_BlankEndpointAndModel_StoredAsNull()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        await Build(db, userId).Update(new UpdateUserSettingsRequest("   ", "", null));

        var dto = await Build(db, userId).Get();
        Assert.Null(dto.ApiBase);
        Assert.Null(dto.Model);
    }

    [Fact]
    public async Task Get_ExposesServerDefaults_AsPlaceholders_WithoutTheServerKey()
    {
        using var db = TestDb.Create();
        var server = new SummarizationOptions
        {
            ApiBase = "https://server/v1",
            Model = "server-model",
            ApiKey = "sk-server-secret",
        };

        var dto = await Build(db, Guid.NewGuid(), server).Get();

        Assert.Equal("https://server/v1", dto.DefaultApiBase);
        Assert.Equal("server-model", dto.DefaultModel);
        Assert.True(dto.ServerHasApiKey);
        // Server key must never be serialised to the client.
        Assert.DoesNotContain("sk-server-secret", System.Text.Json.JsonSerializer.Serialize(dto));
        // No per-user override set, so the user's own fields stay null.
        Assert.Null(dto.ApiBase);
        Assert.False(dto.HasApiKey);
    }

    [Fact]
    public async Task Get_ExposesServerContextWindowDefault_WhenNoOverride()
    {
        using var db = TestDb.Create();
        var dto = await Build(db, Guid.NewGuid(), chat: new ChatOptions { ContextLength = 131072 }).Get();

        Assert.Null(dto.ContextWindow);
        Assert.Equal(131072, dto.DefaultContextWindow);
    }

    [Fact]
    public async Task Put_SetsContextWindowOverride_AndClearsOnNonPositive()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        await Build(db, userId).Update(new UpdateUserSettingsRequest("https://a", "m", null, ContextWindow: 8000));
        Assert.Equal(8000, (await Build(db, userId).Get()).ContextWindow);

        // 0 (or null) clears the override → falls back to the server default.
        await Build(db, userId).Update(new UpdateUserSettingsRequest("https://a", "m", null, ContextWindow: 0));
        Assert.Null((await Build(db, userId).Get()).ContextWindow);
    }

    [Fact]
    public async Task Reasoning_RoundTrips_AndExposesServerDefaults()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var server = new SummarizationOptions { ReasoningEnabled = false, ReasoningEffort = "medium" };

        await Build(db, userId, server).Update(new UpdateUserSettingsRequest(
            "https://a", "m", null, ReasoningEnabled: true, ReasoningEffort: "high"));

        var dto = await Build(db, userId, server).Get();
        Assert.True(dto.ReasoningEnabled);
        Assert.Equal("high", dto.ReasoningEffort);
        Assert.False(dto.DefaultReasoningEnabled);
        Assert.Equal("medium", dto.DefaultReasoningEffort);
    }

    [Fact]
    public async Task Reasoning_NoOverride_ReflectsServerDefaults()
    {
        using var db = TestDb.Create();
        var server = new SummarizationOptions { ReasoningEnabled = true, ReasoningEffort = "low" };

        var dto = await Build(db, Guid.NewGuid(), server).Get();
        Assert.True(dto.ReasoningEnabled);
        Assert.Equal("low", dto.ReasoningEffort);
    }

    [Fact]
    public async Task Settings_AreScopedPerUser()
    {
        using var db = TestDb.Create();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        await Build(db, alice).Update(new UpdateUserSettingsRequest("https://alice", "m", "sk-a"));

        var bobDto = await Build(db, bob).Get();
        Assert.Null(bobDto.ApiBase);
        Assert.False(bobDto.HasApiKey);
    }
}
