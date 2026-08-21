using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;
using Diariz.Api.Services.Llm;

namespace Diariz.Api.Tests;

/// <summary>The number the chat context dial reports against. From 0.221.0 it comes from the model serving
/// chat rather than a per-user override, so none of this takes a user id.</summary>
public class ChatContextResolverTests
{
    private static ChatContextResolver Build(DiarizDbContext db, int serverDefault = 131072) =>
        new(db, Options.Create(new ChatOptions { ContextLength = serverDefault }), new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())));

    private static LlmModel Seed(DiarizDbContext db, int contextLength)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = $"m-{Guid.NewGuid():N}", ApiBase = "http://llm/v1",
            ContextLength = contextLength, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task No_models_configured_uses_the_server_default()
    {
        using var db = TestDb.Create();
        Assert.Equal(131072, await Build(db).ResolveContextWindowAsync());
    }

    [Fact]
    public async Task Uses_the_platform_default_models_window()
    {
        using var db = TestDb.Create();
        var model = Seed(db, 32_768);
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id,
        });
        await db.SaveChangesAsync();

        Assert.Equal(32_768, await Build(db).ResolveContextWindowAsync());
    }

    [Fact]
    public async Task Prefers_the_model_assigned_to_chat_over_the_platform_default()
    {
        // The dial has to report the window of the model that actually answers chat, or it would show a
        // budget from an unrelated model and disagree with the truncation the user really gets.
        using var db = TestDb.Create();
        var chat = Seed(db, 8_192);
        var other = Seed(db, 200_000);
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = other.Id,
        });
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        Assert.Equal(8_192, await Build(db).ResolveContextWindowAsync());
    }

    [Fact]
    public async Task Falls_back_to_the_server_default_for_a_model_with_no_window_set()
    {
        using var db = TestDb.Create();
        var model = Seed(db, 0);
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id,
        });
        await db.SaveChangesAsync();

        Assert.Equal(4096, await Build(db, serverDefault: 4096).ResolveContextWindowAsync());
    }

    // ---- A user's chosen chat model ----

    [Fact]
    public async Task Reports_the_window_of_an_offered_override()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, 8_192);
        var big = Seed(db, 200_000);
        big.ChatEnabled = true;
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        Assert.Equal(200_000, await Build(db).ResolveContextWindowAsync(big.Id));
    }

    [Fact]
    public async Task Ignores_an_override_that_is_not_offered()
    {
        // The dial must report the window that will actually be used. Following a model the settings
        // resolver then refuses would put the gauge and the real truncation back into disagreement - the
        // exact defect this resolver exists to prevent.
        using var db = TestDb.Create();
        var chat = Seed(db, 8_192);
        var secret = Seed(db, 200_000);   // ChatEnabled stays false
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        Assert.Equal(8_192, await Build(db).ResolveContextWindowAsync(secret.Id));
    }

    [Fact]
    public async Task A_null_override_resolves_exactly_as_before()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, 8_192);
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        Assert.Equal(8_192, await Build(db).ResolveContextWindowAsync(null));
    }
}
