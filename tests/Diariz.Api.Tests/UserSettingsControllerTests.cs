using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class UserSettingsControllerTests
{
    private static UserSettingsController Build(DiarizDbContext db, Guid userId) =>
        new(db, new FakeApiKeyProtector()) { ControllerContext = Http.Context(userId) };

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
