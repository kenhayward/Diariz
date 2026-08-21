using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Diariz.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>The picker's source. Every signed-in user reads this, so it must expose no endpoint and no key -
/// which is exactly why it exists instead of reusing the administrator-only model listing.</summary>
public class ChatModelsControllerTests
{
    private static ChatModelsController Build(DiarizDbContext db) =>
        new(new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions()))) { ControllerContext = Http.Context(Guid.NewGuid()) };

    private static LlmModel Seed(DiarizDbContext db, string name, bool chatEnabled, string? displayName = null)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = displayName,
            ApiBase = "http://secret-internal-host:1234/v1", ApiKeyEncrypted = "enc:super-secret",
            ContextLength = 32_768, ChatEnabled = chatEnabled,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task Never_exposes_the_endpoint_or_the_key()
    {
        using var db = TestDb.Create();
        Seed(db, "m", chatEnabled: true);

        var result = await Build(db).List();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain("secret-internal-host", json);
        Assert.DoesNotContain("super-secret", json);
    }

    [Fact]
    public async Task Returns_the_label_and_the_slug()
    {
        using var db = TestDb.Create();
        Seed(db, "qwen3.8-27b@q4_k_xl", chatEnabled: true, displayName: "QWEN 3.8");

        var result = await Build(db).List();
        var dto = Assert.Single(Assert.IsType<List<ChatModelDto>>(result.Value));

        Assert.Equal("QWEN 3.8", dto.Label);
        Assert.Equal("qwen3.8-27b@q4_k_xl", dto.Name);
        Assert.Equal(32_768, dto.ContextLength);
    }

    [Fact]
    public async Task Omits_a_model_that_is_not_offered()
    {
        using var db = TestDb.Create();
        Seed(db, "offered", chatEnabled: true);
        Seed(db, "not-offered", chatEnabled: false);

        var result = await Build(db).List();
        var dtos = Assert.IsType<List<ChatModelDto>>(result.Value);

        Assert.Equal(["offered"], dtos.Select(d => d.Name));
    }

    [Fact]
    public async Task Marks_the_chat_default()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", chatEnabled: false);
        var extra = Seed(db, "extra-model", chatEnabled: true);
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        var result = await Build(db).List();
        var dtos = Assert.IsType<List<ChatModelDto>>(result.Value);

        Assert.True(dtos.Single(d => d.Id == chat.Id).IsDefault);
        Assert.False(dtos.Single(d => d.Id == extra.Id).IsDefault);
    }
}
