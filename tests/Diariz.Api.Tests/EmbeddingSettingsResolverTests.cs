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
            new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions()))));

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
    public async Task Resolve_Disabled_WhenNoEndpointAnywhere()
    {
        using var db = TestDb.Create();
        var cfg = await Build(db, new EmbeddingOptions { ApiBase = "" }, new SummarizationOptions { ApiBase = "" })
            .ResolveAsync();

        Assert.False(cfg.Enabled);
    }
}
