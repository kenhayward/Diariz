using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services.Llm;
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
        return new(db, Options.Create(chat), resolver, new ChatContextResolver(db, Options.Create(chat)),
            new LlmSettingsResolver(db, Options.Create(new LlmDefaultsOptions()),
                Options.Create(new SummarizationOptions()), Protector),
            Options.Create(new DictationOptions()))
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
    public async Task Settings_PersistAcrossContexts()
    {
        // Until 0.221.0 this also proved the user's own API key was ciphertext at rest. The key moved to
        // the model row, and that round trip is proved by Resolver_DecryptsAModelsStoredKey_RoundTrip.
        var userId = await SeedUser();

        await using (var db = fx.CreateDbContext())
            await Settings(db, userId).Update(new UpdateUserSettingsRequest(
                ToolsEnabled: true,
                ToolOverrides: new Dictionary<string, bool> { ["who_said_that"] = false }));

        await using (var verify = fx.CreateDbContext())
        {
            var stored = await verify.UserSettings.SingleAsync(s => s.UserId == userId);
            Assert.True(stored.ChatToolsEnabled);
            Assert.Contains("who_said_that", stored.ChatToolOverridesJson!);
        }
    }

    [Fact]
    public async Task Resolver_DecryptsAModelsStoredKey_RoundTrip()
    {
        // Real Data Protection against real Postgres. The key moved from the user's settings to the model
        // row in 0.221.0, but the round trip is the same guarantee: what is stored is ciphertext, and what
        // reaches the endpoint is the original secret.
        var modelId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.LlmModels.Add(new LlmModel
            {
                Id = modelId,
                Name = $"my-model-{modelId:N}",
                ApiBase = "https://mine/v1",
                ApiKeyEncrypted = Protector.Protect("sk-real-secret"),
                ContextLength = 8192,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Summaries, LlmModelId = modelId });
            await db.SaveChangesAsync();
        }

        try
        {
            await using var verify = fx.CreateDbContext();
            var stored = await verify.LlmModels.SingleAsync(m => m.Id == modelId);
            Assert.DoesNotContain("sk-real-secret", stored.ApiKeyEncrypted!); // real ciphertext at rest

            await using var ctx = fx.CreateDbContext();
            var resolver = new LlmSettingsResolver(
                ctx, Options.Create(new LlmDefaultsOptions()),
                Options.Create(new SummarizationOptions
                {
                    ApiBase = "https://server", ApiKey = "sk-server", Model = "srv",
                }),
                Protector);

            var cfg = await resolver.ResolveAsync(LlmCallKind.Summarize);

            Assert.Equal("https://mine/v1", cfg.ApiBase);
            Assert.Equal(stored.Name, cfg.Model);
            Assert.Equal("sk-real-secret", cfg.ApiKey); // decrypted back to the original
        }
        finally
        {
            // LlmCallAssignment.Group is the primary key, so a leaked row would collide with any later test
            // in this collection that assigns the same group.
            await using var cleanup = fx.CreateDbContext();
            cleanup.LlmCallAssignments.RemoveRange(
                cleanup.LlmCallAssignments.Where(a => a.LlmModelId == modelId));
            await cleanup.SaveChangesAsync();
            cleanup.LlmModels.RemoveRange(cleanup.LlmModels.Where(m => m.Id == modelId));
            await cleanup.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task DeletingUser_CascadesSettings()
    {
        var userId = await SeedUser();
        await using (var db = fx.CreateDbContext())
            await Settings(db, userId).Update(new UpdateUserSettingsRequest(ToolsEnabled: true));

        await using (var db = fx.CreateDbContext())
        {
            var user = await db.Users.FindAsync(userId);
            db.Users.Remove(user!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.UserSettings.AnyAsync(s => s.UserId == userId));
    }

    /// <summary>Phase 3: the placement preference round-trips through real Postgres, and a settings row created
    /// without touching it defaults to SelectedFolder (the migration's column default).</summary>
    [Fact]
    public async Task RecordingPlacement_RoundTrips_AndDefaultsToSelectedFolder()
    {
        var userId = await SeedUser();
        var sectionId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.UserSettings.Add(new UserSettings
            {
                UserId = userId,
                RecordingPlacementMode = RecordingPlacementMode.SpecificFolder,
                RecordingPlacementSectionId = sectionId,
            });
            await db.SaveChangesAsync();
        }

        await using (var verify = fx.CreateDbContext())
        {
            var stored = await verify.UserSettings.SingleAsync(s => s.UserId == userId);
            Assert.Equal(RecordingPlacementMode.SpecificFolder, stored.RecordingPlacementMode);
            Assert.Equal(sectionId, stored.RecordingPlacementSectionId);
        }

        // A row inserted with the DB default (raw SQL bypasses the model default) is SelectedFolder.
        var otherUser = await SeedUser();
        await using (var db = fx.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """INSERT INTO "UserSettings" ("UserId") VALUES ({0})""", otherUser);
            db.ChangeTracker.Clear();
            var stored = await db.UserSettings.SingleAsync(s => s.UserId == otherUser);
            Assert.Equal(RecordingPlacementMode.SelectedFolder, stored.RecordingPlacementMode);
            Assert.Null(stored.RecordingPlacementSectionId);
        }
    }

    /// <summary>The seven LLM columns are really gone from the table, not merely unmapped. An unmapped
    /// column would keep the old data sitting in Postgres and let a future model change quietly resurrect
    /// it; the in-memory provider cannot tell the two apart, which is why this reads information_schema.</summary>
    [Fact]
    public async Task The_per_user_llm_columns_are_dropped()
    {
        await using var db = fx.CreateDbContext();
        var dropped = new[]
        {
            "SummaryApiBase", "SummaryApiKeyEncrypted", "SummaryModel", "ChatContextWindow",
            "LlmTimeoutSeconds", "ReasoningEnabled", "ReasoningEffort",
        };

        var present = await db.Database
            .SqlQuery<string>($"""
                SELECT column_name AS "Value" FROM information_schema.columns
                WHERE table_name = 'UserSettings'
            """)
            .ToListAsync();

        foreach (var column in dropped)
            Assert.DoesNotContain(column, present);

        // A sibling column proves the query itself works - otherwise a typo in the table name would make
        // every assertion above pass against an empty list.
        Assert.Contains("ChatToolsEnabled", present);
    }
}
