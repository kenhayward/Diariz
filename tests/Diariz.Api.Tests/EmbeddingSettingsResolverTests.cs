using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class EmbeddingSettingsResolverTests
{
    private static EmbeddingSettingsResolver Build(
        Diariz.Domain.DiarizDbContext db, EmbeddingOptions emb, SummarizationOptions summary) =>
        new(db, Options.Create(emb), Options.Create(summary), new FakeApiKeyProtector());

    [Fact]
    public async Task Resolve_UsesDedicatedEmbeddingEndpoint_WhenConfigured()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions { ApiBase = "http://emb.test/v1", ApiKey = "sk-emb", Model = "nomic-embed-text", Dimension = 768 };
        var summary = new SummarizationOptions { ApiBase = "http://sum.test/v1", ApiKey = "sk-sum" };

        var cfg = await Build(db, emb, summary).ResolveAsync(Guid.NewGuid());

        Assert.True(cfg.Enabled);
        Assert.Equal("http://emb.test/v1", cfg.ApiBase);
        Assert.Equal("sk-emb", cfg.ApiKey);
        Assert.Equal("nomic-embed-text", cfg.Model); // server-pinned
        Assert.Equal(768, cfg.Dimension);
    }

    [Fact]
    public async Task Resolve_FallsBackToUserSummaryEndpoint_WhenNoEmbeddingEndpoint()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings
        {
            UserId = userId, SummaryApiBase = "http://user.test/v1", SummaryApiKeyEncrypted = "enc:user-key",
        });
        await db.SaveChangesAsync();
        var emb = new EmbeddingOptions { ApiBase = "", Model = "nomic-embed-text" };
        var summary = new SummarizationOptions { ApiBase = "http://server.test/v1", ApiKey = "sk-server" };

        var cfg = await Build(db, emb, summary).ResolveAsync(userId);

        Assert.Equal("http://user.test/v1", cfg.ApiBase); // the user's own endpoint wins over the server default
        Assert.Equal("user-key", cfg.ApiKey);             // decrypted
        Assert.Equal("nomic-embed-text", cfg.Model);
    }

    [Fact]
    public async Task Resolve_FallsBackToServerSummaryDefaults_WhenUserHasNone()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions { ApiBase = "" };
        var summary = new SummarizationOptions { ApiBase = "http://server.test/v1", ApiKey = "sk-server" };

        var cfg = await Build(db, emb, summary).ResolveAsync(Guid.NewGuid());

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

        var cfg = await Build(db, emb, new SummarizationOptions()).ResolveAsync(Guid.NewGuid());

        Assert.Equal("search_query: ", cfg.QueryPrefix);
        Assert.Equal("search_document: ", cfg.DocumentPrefix);
    }

    [Fact]
    public async Task Resolve_AllowsEmptyPrefixes_ForNonNomicModels()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions { ApiBase = "http://emb.test/v1", QueryPrefix = "", DocumentPrefix = "" };

        var cfg = await Build(db, emb, new SummarizationOptions()).ResolveAsync(Guid.NewGuid());

        Assert.Equal("", cfg.QueryPrefix);
        Assert.Equal("", cfg.DocumentPrefix);
    }

    [Fact]
    public async Task Resolve_Disabled_WhenNoEndpointAnywhere()
    {
        using var db = TestDb.Create();
        var cfg = await Build(db, new EmbeddingOptions { ApiBase = "" }, new SummarizationOptions { ApiBase = "" })
            .ResolveAsync(Guid.NewGuid());

        Assert.False(cfg.Enabled);
    }
}
