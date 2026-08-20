using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The SET NULL cascade on a user's chat-model pick.
///
/// This cannot live in the unit project: the in-memory provider enforces no foreign keys at all, so a test
/// there would pass whatever the delete behaviour actually was - including the RESTRICT that would make a
/// model undeletable the moment one person picked it.</summary>
[Collection(IntegrationCollection.Name)]
public class ChatModelSelectionSchemaTests(ContainersFixture fx)
{
    private static LlmModel NewModel() => new()
    {
        Id = Guid.NewGuid(),
        Name = $"m-{Guid.NewGuid():N}",
        ApiBase = "http://llm.test/v1",
        ContextLength = 8192,
        ChatEnabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Deleting_a_model_a_user_picked_succeeds_and_clears_the_pick()
    {
        var userId = Guid.NewGuid();
        var model = NewModel();

        await using (var db = fx.CreateDbContext())
        {
            Users.Ensure(db, userId);
            db.LlmModels.Add(model);
            db.UserSettings.Add(new UserSettings { UserId = userId, ChatModelId = model.Id });
            await db.SaveChangesAsync();
        }

        // A fresh context, so the settings row is untracked and the DATABASE's rule is what decides.
        // With the row tracked, EF would null the FK itself and the constraint would never fire - which is
        // exactly how PlatformSettings.DefaultLlmModelId escapes its own RESTRICT.
        await using (var db = fx.CreateDbContext())
        {
            db.LlmModels.Remove(await db.LlmModels.SingleAsync(m => m.Id == model.Id));
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var settings = await db.UserSettings.AsNoTracking().SingleAsync(s => s.UserId == userId);
            Assert.Null(settings.ChatModelId);
        }
    }

    [Fact]
    public async Task A_display_name_and_the_chat_flag_round_trip()
    {
        var model = NewModel();
        model.DisplayName = "QWEN 3.8";

        await using (var db = fx.CreateDbContext())
        {
            db.LlmModels.Add(model);
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var stored = await db.LlmModels.AsNoTracking().SingleAsync(m => m.Id == model.Id);
            Assert.Equal("QWEN 3.8", stored.DisplayName);
            Assert.Equal("QWEN 3.8", stored.Label);
            Assert.True(stored.ChatEnabled);
        }
    }
}
