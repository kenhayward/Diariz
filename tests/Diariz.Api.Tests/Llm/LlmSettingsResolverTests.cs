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
            new FakeApiKeyProtector(), new ChatModelCatalog(db), Options.Create(new ChatOptions()));

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
    public async Task Keeps_the_tuned_platform_timeout_whether_or_not_a_model_row_exists()
    {
        // REVERSED in 0.235.1. This used to assert the opposite for the second case - a model row made the
        // platform timeout inert - which was the 0.221.0 position that the timeout had become a per-model
        // parameter. It could not survive contact with the UI: the Settings control still promises a
        // "platform-wide request timeout for every AI call", so an administrator who raised it to 600 for a
        // slow local model got 120 and a failure that looks like a dead endpoint. Either the control had to
        // go or the value had to be honoured; honouring it keeps per-model tuning intact, because the
        // platform value sits BELOW a model's own layers.
        using var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 900 });
        await db.SaveChangesAsync();

        Assert.Equal(900, (await Build(db).ResolveAsync(LlmCallKind.Summarize)).TimeoutSeconds);

        var model = Seed(db, "m", "http://m/v1");
        var ps = db.PlatformSettings.First();
        ps.DefaultLlmModelId = model.Id;
        await db.SaveChangesAsync();

        Assert.Equal(900, (await Build(db).ResolveAsync(LlmCallKind.Summarize)).TimeoutSeconds);
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

    private static LlmModel Seed(DiarizDbContext db, string name, string apiBase,
        bool chatEnabled = false, int contextLength = 8192)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = name, ApiBase = apiBase, ContextLength = contextLength,
            ChatEnabled = chatEnabled,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.SaveChanges();
        return m;
    }

    // ---- A user's chosen chat model ----

    /// <summary>Seeds a chat-assigned model plus one the administrator has offered in the picker.</summary>
    private static (LlmModel chat, LlmModel offered) SeedChatPair(DiarizDbContext db)
    {
        var chat = Seed(db, "chat-model", "http://chat/v1", contextLength: 8_192);
        var offered = Seed(db, "big-model", "http://big/v1", chatEnabled: true, contextLength: 200_000);
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        db.SaveChanges();
        return (chat, offered);
    }

    [Fact]
    public async Task Honours_a_chat_model_override_the_administrator_offers()
    {
        using var db = TestDb.Create();
        var (_, offered) = SeedChatPair(db);

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, offered.Id);

        Assert.Equal("big-model", cfg.Model);
        Assert.Equal("http://big/v1", cfg.ApiBase);
    }

    [Fact]
    public async Task Ignores_an_override_for_a_model_that_is_not_offered_for_chat()
    {
        using var db = TestDb.Create();
        var chat = Seed(db, "chat-model", "http://chat/v1");
        var secret = Seed(db, "expensive-model", "http://secret/v1");   // ChatEnabled stays false
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = chat.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, secret.Id);

        Assert.Equal("chat-model", cfg.Model);
    }

    [Fact]
    public async Task Ignores_an_override_for_a_chat_title()
    {
        // Titling is background housekeeping the user never sees, so it stays on the chat default - a slow
        // or expensive picked model has no business generating a one-line title.
        using var db = TestDb.Create();
        var (_, offered) = SeedChatPair(db);

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatTitle, offered.Id);

        Assert.Equal("chat-model", cfg.Model);
    }

    [Fact]
    public async Task Ignores_an_override_for_a_non_chat_call_kind()
    {
        using var db = TestDb.Create();
        var summaries = Seed(db, "summary-model", "http://sum/v1");
        var big = Seed(db, "big-model", "http://big/v1", chatEnabled: true);
        db.LlmCallAssignments.Add(
            new LlmCallAssignment { Group = LlmCallGroup.Summaries, LlmModelId = summaries.Id });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.Summarize, big.Id);

        Assert.Equal("summary-model", cfg.Model);
    }

    [Fact]
    public async Task Sizes_the_context_budget_from_the_overridden_model()
    {
        // The budget is what actually truncates transcript text. Were it still taken from the default
        // model, picking a 200k model would silently keep cutting context at the small model's size.
        using var db = TestDb.Create();
        var (_, offered) = SeedChatPair(db);

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, offered.Id);

        Assert.Equal(LlmContextBudget.CharsFor(200_000), cfg.ContextCharBudget);
    }

    [Fact]
    public async Task Applies_the_overridden_models_own_chat_parameters()
    {
        // Parameters are per (model, group), so switching model must bring that model's Chat overrides -
        // not the default model's.
        using var db = TestDb.Create();
        var (_, offered) = SeedChatPair(db);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = offered.Id, Group = LlmCallGroup.Chat,
            ParametersJson = """{"temperature":0.91}""",
        });
        await db.SaveChangesAsync();

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, offered.Id);

        Assert.Equal(0.91, cfg.Parameters.Temperature);
    }

    [Fact]
    public async Task A_null_override_resolves_exactly_as_before()
    {
        using var db = TestDb.Create();
        var (_, _) = SeedChatPair(db);

        var cfg = await Build(db).ResolveAsync(LlmCallKind.ChatMessage, null);

        Assert.Equal("chat-model", cfg.Model);
    }

    [Fact]
    public async Task Honours_the_administrators_platform_timeout_for_a_model_row()
    {
        // Regression, 0.235.1. The Settings control promises "platform-wide request timeout for EVERY AI
        // call", but the value was read only on the environment-fallback path - so the moment a deployment
        // configured its first model it became inert, and every call silently reverted to the shipped 120.
        // A 27b model on a local server needs more than that, and the failure looks like a dead endpoint.
        using var db = TestDb.Create();
        var model = Seed(db, "big-model", "http://big/v1");
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id, LlmTimeoutSeconds = 600,
        });
        await db.SaveChangesAsync();

        Assert.Equal(600, (await Build(db).ResolveAsync(LlmCallKind.Summarize)).TimeoutSeconds);
    }

    [Fact]
    public async Task A_models_own_timeout_still_beats_the_platform_one()
    {
        // The platform value is a floor for models that say nothing, not an override. Per-model tuning is
        // the whole point of the parameter drawer, and it has to keep winning.
        using var db = TestDb.Create();
        var model = Seed(db, "big-model", "http://big/v1");
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase,
            ParametersJson = "{\"timeout_seconds\":90}",
        });
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id, LlmTimeoutSeconds = 600,
        });
        await db.SaveChangesAsync();

        Assert.Equal(90, (await Build(db).ResolveAsync(LlmCallKind.Summarize)).TimeoutSeconds);
    }

    [Fact]
    public async Task Leaves_the_configured_default_alone_when_the_admin_never_changed_it()
    {
        // An operator can set LlmDefaults__TimeoutSeconds in configuration. The platform row defaults to 120
        // and must not silently outrank that just by existing - only a value the admin actually changed
        // should speak.
        using var db = TestDb.Create();
        var model = Seed(db, "big-model", "http://big/v1");
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id,
            LlmTimeoutSeconds = PlatformSettings.DefaultLlmTimeoutSeconds,
        });
        await db.SaveChangesAsync();
        var defaults = new LlmDefaultsOptions { TimeoutSeconds = 45 };

        Assert.Equal(45, (await Build(db, defaults).ResolveAsync(LlmCallKind.Summarize)).TimeoutSeconds);
    }
}
