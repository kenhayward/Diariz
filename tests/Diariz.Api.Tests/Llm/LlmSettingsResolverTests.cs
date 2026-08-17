using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests.Llm;

/// <summary>The four-layer walk that decides which model serves a call and with what parameters.
/// Configuration is platform-wide from 0.221.0, so nothing here takes a user id.</summary>
public class LlmSettingsResolverTests
{
    private static LlmSettingsResolver Build(
        DiarizDbContext db, LlmDefaultsOptions? defaults = null, SummarizationOptions? summary = null) =>
        new(db, Options.Create(defaults ?? new LlmDefaultsOptions()),
            Options.Create(summary ?? new SummarizationOptions { ApiBase = "http://env/v1", Model = "env-model" }),
            new FakeApiKeyProtector(), Options.Create(new ChatOptions()));

    [Fact]
    public async Task Falls_back_to_the_environment_model_when_no_rows_exist()
    {
        using var db = TestDb.Create();
        var cfg = await Build(db).ResolveAsync(LlmCallKind.Summarize);

        Assert.Equal("http://env/v1", cfg.ApiBase);
        Assert.Equal("env-model", cfg.Model);
        Assert.Equal(0.3, cfg.Parameters.Temperature);
    }

    [Fact]
    public async Task Does_not_persist_the_environment_fallback()
    {
        // Persisting it would resurrect a row the admin deliberately deleted - the same defect as the
        // user-role backfill that kept undoing demotions.
        using var db = TestDb.Create();
        await Build(db).ResolveAsync(LlmCallKind.Summarize);

        Assert.Empty(db.LlmModels);
    }

    [Fact]
    public async Task Prefers_the_group_assignment_over_the_platform_default()
    {
        using var db = TestDb.Create();
        var fast = Seed(db, "fast-model", "http://fast/v1");
        var big = Seed(db, "big-model", "http://big/v1");
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, DefaultLlmModelId = big.Id });
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Tags, LlmModelId = fast.Id });
        await db.SaveChangesAsync();

        Assert.Equal("fast-model", (await Build(db).ResolveAsync(LlmCallKind.Tags)).Model);
        Assert.Equal("big-model", (await Build(db).ResolveAsync(LlmCallKind.Summarize)).Model);
    }

    [Fact]
    public async Task Walks_all_four_layers_in_order()
    {
        using var db = TestDb.Create();
        var model = Seed(db, "m", "http://m/v1");
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase,
            ParametersJson = """{"temperature":0.5,"top_p":0.9}""",
        });
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.Tags,
            ParametersJson = """{"temperature":0.0}""",
        });
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id });
        await db.SaveChangesAsync();

        var tags = await Build(db).ResolveAsync(LlmCallKind.Tags);
        Assert.Equal(0.0, tags.Parameters.Temperature);    // group override
        Assert.Equal(0.9, tags.Parameters.TopP);           // model base
        Assert.Equal(120, tags.Parameters.TimeoutSeconds); // app default

        var chat = await Build(db).ResolveAsync(LlmCallKind.ChatMessage);
        Assert.Equal(0.5, chat.Parameters.Temperature);    // model base, no group override
    }

    [Fact]
    public async Task A_group_override_can_omit_a_parameter_the_model_base_sets()
    {
        using var db = TestDb.Create();
        var model = Seed(db, "m", "http://m/v1");
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase,
            ParametersJson = """{"temperature":0.5}""",
        });
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.Translation,
            ParametersJson = """{"temperature":null}""",
        });
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id });
        await db.SaveChangesAsync();

        Assert.Null((await Build(db).ResolveAsync(LlmCallKind.Translation)).Parameters.Temperature);
    }

    [Fact]
    public async Task Resolves_a_groupless_kind_without_sampling_parameters()
    {
        using var db = TestDb.Create();
        var cfg = await Build(db).ResolveAsync(LlmCallKind.Embedding);

        Assert.Equal("http://env/v1", cfg.ApiBase);
        Assert.Equal(120, cfg.TimeoutSeconds);
    }

    [Fact]
    public async Task Uses_the_models_own_key_and_context_window()
    {
        using var db = TestDb.Create();
        var model = Seed(db, "m", "http://m/v1");
        model.ApiKeyEncrypted = new FakeApiKeyProtector().Protect("sk-model");
        model.ContextLength = 32_768;
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.Summarize);

        Assert.Equal("sk-model", cfg.ApiKey);
        Assert.Equal(LlmContextBudget.CharsFor(32_768), cfg.ContextCharBudget);
    }

    [Fact]
    public async Task Keeps_the_tuned_platform_timeout_only_while_falling_back_to_the_environment()
    {
        // The admin's platform timeout could not be migrated into a model row (the endpoint lives in
        // configuration, not the database), so the fallback honours it - and a real model does not.
        using var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 900 });
        await db.SaveChangesAsync();

        Assert.Equal(900, (await Build(db).ResolveAsync(LlmCallKind.Summarize)).TimeoutSeconds);

        var model = Seed(db, "m", "http://m/v1");
        var ps = db.PlatformSettings.First();
        ps.DefaultLlmModelId = model.Id;
        await db.SaveChangesAsync();

        Assert.Equal(120, (await Build(db).ResolveAsync(LlmCallKind.Summarize)).TimeoutSeconds);
    }

    [Fact]
    public async Task Reports_reasoning_effort_only_when_reasoning_is_enabled()
    {
        using var db = TestDb.Create();
        var model = Seed(db, "m", "http://m/v1");
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase,
            ParametersJson = """{"reasoning_effort":"xhigh"}""",
        });
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id });
        await db.SaveChangesAsync();

        // App default leaves reasoning off, so the effort is configured but not reported.
        Assert.Null((await Build(db).ResolveAsync(LlmCallKind.Summarize)).ReasoningEffort);

        var on = new LlmDefaultsOptions { ReasoningEnabled = true };
        Assert.Equal("xhigh", (await Build(db, on).ResolveAsync(LlmCallKind.Summarize)).ReasoningEffort);
    }

    [Fact]
    public async Task Is_disabled_when_nothing_is_configured_anywhere()
    {
        using var db = TestDb.Create();
        var cfg = await Build(db, summary: new SummarizationOptions { ApiBase = "" })
            .ResolveAsync(LlmCallKind.Summarize);

        Assert.False(cfg.Enabled);
    }

    [Fact]
    public async Task Budgets_context_far_above_the_old_hard_coded_folder_cap()
    {
        // Regression guard for the reported bug: a folder summary used to be capped at 24,000 chars, which
        // dropped whole meetings out of larger folders. Against the default window it must be several times
        // that - if this ever drops back, folder roll-ups start silently omitting meetings again.
        using var db = TestDb.Create();

        Assert.True((await Build(db).ResolveAsync(LlmCallKind.SectionSummary)).ContextCharBudget > 200_000);
    }

    [Fact]
    public async Task Carries_the_stream_usage_toggle_from_platform_settings()
    {
        using var db = TestDb.Create();
        Assert.True((await Build(db).ResolveAsync(LlmCallKind.ChatMessage)).IncludeStreamUsage);

        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, LlmStreamUsageEnabled = false,
        });
        await db.SaveChangesAsync();

        Assert.False((await Build(db).ResolveAsync(LlmCallKind.ChatMessage)).IncludeStreamUsage);
    }

    private static LlmModel Seed(DiarizDbContext db, string name, string apiBase)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = name, ApiBase = apiBase, ContextLength = 8192,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.SaveChanges();
        return m;
    }
}
