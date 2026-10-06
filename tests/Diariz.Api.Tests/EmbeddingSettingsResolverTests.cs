using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>The embedding transport chain. From 0.221.0 the fallback is the platform's default model rather
/// than the recording owner's own endpoint, so none of this takes a user id.</summary>
public class EmbeddingSettingsResolverTests
{
    private static EmbeddingSettingsResolver Build(
        Diariz.Domain.DiarizDbContext db, EmbeddingOptions emb, SummarizationOptions summary) =>
        new(db, Options.Create(emb), new LlmSettingsResolver(
            db, Options.Create(new LlmDefaultsOptions()), Options.Create(summary), new FakeApiKeyProtector(),
            new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions()))), new FakeApiKeyProtector());

    /// <summary>The endpoint an administrator saved on the AI models page (issue #836).</summary>
    private static void SavePlatformEndpoint(Diariz.Domain.DiarizDbContext db, string apiBase, string? key = null)
    {
        var row = db.PlatformSettings.Find(PlatformSettings.SingletonId);
        if (row is null)
        {
            row = new PlatformSettings { Id = PlatformSettings.SingletonId };
            db.PlatformSettings.Add(row);
        }
        row.EmbeddingApiBase = apiBase;
        row.EmbeddingApiKeyEncrypted = key is null ? null : $"enc:{key}";
        db.SaveChanges();
    }

    private static LlmModel Seed(Diariz.Domain.DiarizDbContext db, string apiBase, string? key = null)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = $"m-{Guid.NewGuid():N}", ApiBase = apiBase,
            ApiKeyEncrypted = key is null ? null : $"enc:{key}", ContextLength = 8192,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = m.Id,
        });
        db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task Resolve_UsesDedicatedEmbeddingEndpoint_WhenConfigured()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions
        {
            ApiBase = "http://emb.test/v1", ApiKey = "sk-emb", Model = "nomic-embed-text", Dimension = 768,
        };
        var summary = new SummarizationOptions { ApiBase = "http://sum.test/v1", ApiKey = "sk-sum" };

        var cfg = await Build(db, emb, summary).ResolveAsync();

        Assert.True(cfg.Enabled);
        Assert.Equal("http://emb.test/v1", cfg.ApiBase);
        Assert.Equal("sk-emb", cfg.ApiKey);
        Assert.Equal("nomic-embed-text", cfg.Model); // server-pinned
        Assert.Equal(768, cfg.Dimension);
        Assert.Equal(EmbeddingEndpointSource.Server, cfg.Source);
    }

    [Fact]
    public async Task Resolve_FallsBackToThePlatformDefaultModel_WhenNoEmbeddingEndpoint()
    {
        using var db = TestDb.Create();
        Seed(db, "http://platform.test/v1", "model-key");
        var emb = new EmbeddingOptions { ApiBase = "", Model = "nomic-embed-text" };
        var summary = new SummarizationOptions { ApiBase = "http://server.test/v1", ApiKey = "sk-server" };

        var cfg = await Build(db, emb, summary).ResolveAsync();

        Assert.Equal("http://platform.test/v1", cfg.ApiBase); // the configured model beats the env default
        Assert.Equal("model-key", cfg.ApiKey);                // decrypted
        Assert.Equal("nomic-embed-text", cfg.Model);          // still server-pinned: the vector column is fixed
    }

    [Fact]
    public async Task Resolve_FallsBackToServerSummaryDefaults_WhenNoModelIsConfigured()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions { ApiBase = "" };
        var summary = new SummarizationOptions { ApiBase = "http://server.test/v1", ApiKey = "sk-server" };

        var cfg = await Build(db, emb, summary).ResolveAsync();

        Assert.Equal("http://server.test/v1", cfg.ApiBase);
        Assert.Equal("sk-server", cfg.ApiKey);
    }

    [Fact]
    public async Task Resolve_CarriesTaskPrefixes_FromOptions()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions
        {
            ApiBase = "http://emb.test/v1", QueryPrefix = "search_query: ", DocumentPrefix = "search_document: ",
        };

        var cfg = await Build(db, emb, new SummarizationOptions()).ResolveAsync();

        Assert.Equal("search_query: ", cfg.QueryPrefix);
        Assert.Equal("search_document: ", cfg.DocumentPrefix);
    }

    [Fact]
    public async Task Resolve_AllowsEmptyPrefixes_ForNonNomicModels()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions { ApiBase = "http://emb.test/v1", QueryPrefix = "", DocumentPrefix = "" };

        var cfg = await Build(db, emb, new SummarizationOptions()).ResolveAsync();

        Assert.Equal("", cfg.QueryPrefix);
        Assert.Equal("", cfg.DocumentPrefix);
    }

    [Fact]
    public async Task Resolve_UsesTheEmbeddingOptionTimeout_ForADedicatedEndpoint()
    {
        // A dedicated embeddings endpoint is its own service, so it keeps its own deadline rather than
        // inheriting whatever the chat model was tuned to.
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions { ApiBase = "http://emb.test/v1", TimeoutSeconds = 45 };
        Seed(db, "http://platform.test/v1");

        Assert.Equal(45, (await Build(db, emb, new SummarizationOptions()).ResolveAsync()).TimeoutSeconds);
    }

    [Fact]
    public async Task Resolve_TakesTheModelsTimeout_WhenItSuppliesTheEndpoint()
    {
        // Sharing the endpoint means sharing its deadline - otherwise embeddings quietly disagree with
        // every other call to the same server.
        using var db = TestDb.Create();
        var model = Seed(db, "http://platform.test/v1");
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase,
            ParametersJson = """{"timeout_seconds":300}""",
        });
        await db.SaveChangesAsync();

        var emb = new EmbeddingOptions { ApiBase = "", TimeoutSeconds = 45 };

        Assert.Equal(300, (await Build(db, emb, new SummarizationOptions()).ResolveAsync()).TimeoutSeconds);
    }

    [Fact]
    public async Task Resolve_PlatformEndpoint_BeatsTheServerEndpointAndTheDefaultModel()
    {
        // Issue #836: the page is where an administrator fixes this without a redeploy, so a saved endpoint
        // must win over EMBED_API_BASE as well as over the default model.
        using var db = TestDb.Create();
        Seed(db, "http://platform-default.test/v1", "model-key");
        SavePlatformEndpoint(db, "http://page.test/v1", "page-key");
        var emb = new EmbeddingOptions { ApiBase = "http://env.test/v1", ApiKey = "env-key", TimeoutSeconds = 45 };

        var cfg = await Build(db, emb, new SummarizationOptions()).ResolveAsync();

        Assert.Equal("http://page.test/v1", cfg.ApiBase);
        Assert.Equal("page-key", cfg.ApiKey);                  // decrypted
        Assert.Equal(EmbeddingEndpointSource.Platform, cfg.Source);
        Assert.Equal(45, cfg.TimeoutSeconds);                  // a dedicated endpoint keeps the embedding deadline
    }

    [Fact]
    public async Task Resolve_PlatformEndpointWithNoKey_SendsNoKey_EvenWhenTheServerHasOne()
    {
        // A saved endpoint brings its own (possibly blank) key; leaking the env key to a different host would
        // send a credential somewhere it was never meant to go.
        using var db = TestDb.Create();
        SavePlatformEndpoint(db, "http://page.test/v1");
        var emb = new EmbeddingOptions { ApiBase = "http://env.test/v1", ApiKey = "env-key" };

        var cfg = await Build(db, emb, new SummarizationOptions()).ResolveAsync();

        Assert.Equal("", cfg.ApiKey);
    }

    [Fact]
    public async Task Resolve_BlankPlatformEndpoint_IsIgnored()
    {
        using var db = TestDb.Create();
        SavePlatformEndpoint(db, "   ");
        var emb = new EmbeddingOptions { ApiBase = "http://env.test/v1" };

        var cfg = await Build(db, emb, new SummarizationOptions()).ResolveAsync();

        Assert.Equal("http://env.test/v1", cfg.ApiBase);
        Assert.Equal(EmbeddingEndpointSource.Server, cfg.Source);
    }

    [Fact]
    public async Task Resolve_ReportsWhereTheEndpointCameFrom()
    {
        // The page shows this, and a "following the default model" source is the warning that would have
        // caught #836 before it broke indexing.
        using var db = TestDb.Create();
        Seed(db, "http://platform.test/v1");
        var viaModel = await Build(db, new EmbeddingOptions { ApiBase = "" }, new SummarizationOptions()).ResolveAsync();
        Assert.Equal(EmbeddingEndpointSource.DefaultModel, viaModel.Source);

        using var empty = TestDb.Create();
        var none = await Build(empty, new EmbeddingOptions { ApiBase = "" }, new SummarizationOptions { ApiBase = "" })
            .ResolveAsync();
        Assert.Equal(EmbeddingEndpointSource.None, none.Source);
    }

    [Fact]
    public async Task Resolve_Disabled_WhenNoEndpointAnywhere()
    {
        using var db = TestDb.Create();
        var cfg = await Build(db, new EmbeddingOptions { ApiBase = "" }, new SummarizationOptions { ApiBase = "" })
            .ResolveAsync();

        Assert.False(cfg.Enabled);
    }
}
