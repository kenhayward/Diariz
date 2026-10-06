using System.Net;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>The embedding card on the AI models page (issue #836): where embeddings actually go, the endpoint an
/// administrator can save over it, and a test that would have shown the 404 before it broke indexing. The
/// <c>ManagePlatform</c> gate is proved over the real pipeline in <c>EmbeddingSettingsAuthTests</c>, for the reason
/// <see cref="LlmModelsControllerTests"/> gives.</summary>
public class EmbeddingSettingsControllerTests
{
    private sealed record Harness(
        EmbeddingSettingsController Controller, FakeEmbeddingClient Client, FakeJobQueue Queue);

    private static Harness Build(DiarizDbContext db, EmbeddingOptions? emb = null, FakeEmbeddingClient? client = null)
    {
        emb ??= new EmbeddingOptions { ApiBase = "", Model = "nomic-embed-text", Dimension = 4 };
        var protector = new FakeApiKeyProtector();
        var resolver = new EmbeddingSettingsResolver(db, Options.Create(emb), new LlmSettingsResolver(
            db, Options.Create(new LlmDefaultsOptions()), Options.Create(new SummarizationOptions()), protector,
            new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions()))), protector);
        client ??= new FakeEmbeddingClient { Vectors = [new[] { 0.1f, 0.2f, 0.3f, 0.4f }] };
        var queue = new FakeJobQueue();
        var controller = new EmbeddingSettingsController(
            db, protector, Options.Create(emb), resolver, client, queue,
            NullLogger<EmbeddingSettingsController>.Instance)
        { ControllerContext = Http.Context(Guid.NewGuid()) };
        return new Harness(controller, client, queue);
    }

    private static LlmModel SeedDefaultModel(DiarizDbContext db, string apiBase)
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = "chat-model", ApiBase = apiBase, ContextLength = 8192,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, DefaultLlmModelId = m.Id });
        db.SaveChanges();
        return m;
    }

    private static T Ok<T>(ActionResult<T> result) =>
        Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task Get_ReportsTheDefaultModelFallback_SoThePageCanWarn()
    {
        using var db = TestDb.Create();
        SeedDefaultModel(db, "http://chat.test/v1");

        var dto = await Build(db).Controller.Get();

        Assert.Equal("DefaultModel", dto.Source);
        Assert.Equal("http://chat.test/v1", dto.EffectiveApiBase);
        Assert.Equal("nomic-embed-text", dto.Model);
        Assert.Equal(4, dto.Dimension);
        Assert.Null(dto.SavedApiBase);
        Assert.Null(dto.ServerApiBase);
    }

    [Fact]
    public async Task Get_ShowsTheServerEndpoint_AndNeverAKey()
    {
        using var db = TestDb.Create();
        var emb = new EmbeddingOptions { ApiBase = "http://env.test/v1", ApiKey = "env-secret", Model = "m", Dimension = 4 };

        var dto = await Build(db, emb).Controller.Get();

        Assert.Equal("Server", dto.Source);
        Assert.Equal("http://env.test/v1", dto.ServerApiBase);
        Assert.DoesNotContain("env-secret", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public async Task Update_SavesAnEncryptedKey_AndTheEndpointTakesOver()
    {
        using var db = TestDb.Create();
        SeedDefaultModel(db, "http://chat.test/v1");
        var h = Build(db);

        var saved = Ok(await h.Controller.Update(new UpdateEmbeddingSettingsRequest(" http://emb.test/v1 ", "sk-1")));

        Assert.Equal("Platform", saved.Settings.Source);
        Assert.Equal("http://emb.test/v1", saved.Settings.SavedApiBase);
        Assert.True(saved.Settings.SavedHasApiKey);
        var row = db.PlatformSettings.Single();
        Assert.Equal("enc:sk-1", row.EmbeddingApiKeyEncrypted); // never stored in the clear
    }

    [Fact]
    public async Task Update_NullKey_KeepsTheSavedKey()
    {
        using var db = TestDb.Create();
        var h = Build(db);
        await h.Controller.Update(new UpdateEmbeddingSettingsRequest("http://emb.test/v1", "sk-1"));

        await h.Controller.Update(new UpdateEmbeddingSettingsRequest("http://emb.test/v1", null));

        Assert.Equal("enc:sk-1", db.PlatformSettings.Single().EmbeddingApiKeyEncrypted);
    }

    [Fact]
    public async Task Update_EmptyKey_ClearsIt()
    {
        using var db = TestDb.Create();
        var h = Build(db);
        await h.Controller.Update(new UpdateEmbeddingSettingsRequest("http://emb.test/v1", "sk-1"));

        await h.Controller.Update(new UpdateEmbeddingSettingsRequest("http://emb.test/v1", ""));

        Assert.Null(db.PlatformSettings.Single().EmbeddingApiKeyEncrypted);
    }

    [Fact]
    public async Task Update_BlankEndpoint_ClearsTheEndpointAndItsKey()
    {
        // Clearing hands control back to EMBED_API_BASE / the default model. Leaving the key behind would attach
        // it to whatever endpoint is saved next.
        using var db = TestDb.Create();
        var h = Build(db);
        await h.Controller.Update(new UpdateEmbeddingSettingsRequest("http://emb.test/v1", "sk-1"));

        var saved = Ok(await h.Controller.Update(new UpdateEmbeddingSettingsRequest("  ", null)));

        var row = db.PlatformSettings.Single();
        Assert.Null(row.EmbeddingApiBase);
        Assert.Null(row.EmbeddingApiKeyEncrypted);
        Assert.Equal("None", saved.Settings.Source);
    }

    [Theory]
    [InlineData("emb.test/v1")]
    [InlineData("ftp://emb.test/v1")]
    [InlineData("not a url")]
    public async Task Update_RejectsAnythingButAnAbsoluteHttpUrl(string apiBase)
    {
        using var db = TestDb.Create();

        var result = await Build(db).Controller.Update(new UpdateEmbeddingSettingsRequest(apiBase, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null(db.PlatformSettings.SingleOrDefault()?.EmbeddingApiBase);
    }

    [Fact]
    public async Task Update_QueuesEveryUnindexedRecording_SoAFixedEndpointCatchesUp()
    {
        // While the endpoint was wrong every new recording failed to index. The startup backfill only runs on a
        // restart, so saving the fix has to queue them or they stay keyword-only until the next redeploy.
        using var db = TestDb.Create();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Title = "Ada sync", BlobKey = "k" };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 });
        db.SaveChanges();
        var h = Build(db);

        var saved = Ok(await h.Controller.Update(new UpdateEmbeddingSettingsRequest("http://emb.test/v1", null)));

        Assert.Equal(1, saved.ReindexQueued);
        Assert.Equal(rec.Id, Assert.Single(h.Queue.EmbeddingEnqueued).RecordingId);
    }

    [Fact]
    public async Task Update_ToNoEndpointAtAll_QueuesNothing()
    {
        using var db = TestDb.Create();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Title = "Ada sync", BlobKey = "k" };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 });
        db.SaveChanges();
        var h = Build(db);

        var saved = Ok(await h.Controller.Update(new UpdateEmbeddingSettingsRequest("", null)));

        Assert.Equal(0, saved.ReindexQueued);
        Assert.Empty(h.Queue.EmbeddingEnqueued);
    }

    [Fact]
    public async Task Test_EmbedsASampleAgainstTheEffectiveEndpoint_AndReportsTheDimension()
    {
        using var db = TestDb.Create();
        var h = Build(db);
        await h.Controller.Update(new UpdateEmbeddingSettingsRequest("http://emb.test/v1", "sk-1"));

        var result = await h.Controller.Test();

        Assert.True(result.Ok);
        Assert.Equal(4, result.Dimension);
        Assert.Equal(4, result.ExpectedDimension);
        Assert.Equal("http://emb.test/v1", h.Client.LastConfig!.ApiBase);
        Assert.Equal("sk-1", h.Client.LastConfig.ApiKey);
        Assert.Single(h.Client.LastInputs!);
    }

    [Fact]
    public async Task Test_FailsWhenTheVectorIsTheWrongSize()
    {
        // A reachable endpoint serving a different embedding model answers 200 - and then every insert into the
        // dimension-pinned column fails. The test is the only place that shows it before it bites.
        using var db = TestDb.Create();
        var client = new FakeEmbeddingClient { Vectors = [new[] { 1f, 2f }] };
        var h = Build(db, new EmbeddingOptions { ApiBase = "http://env.test/v1", Model = "m", Dimension = 4 }, client);

        var result = await h.Controller.Test();

        Assert.False(result.Ok);
        Assert.Equal(2, result.Dimension);
        Assert.Contains("4", result.Error);
    }

    [Fact]
    public async Task Test_ReportsTheEndpointsStatusCode_OnAnHttpFailure()
    {
        using var db = TestDb.Create();
        var client = new FakeEmbeddingClient
        {
            ThrowOnCall = new HttpRequestException("404 (Not Found): model not found", null, HttpStatusCode.NotFound),
        };
        SeedDefaultModel(db, "http://chat.test/v1");
        var h = Build(db, client: client);

        var result = await h.Controller.Test();

        Assert.False(result.Ok);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("model not found", result.Error);
        Assert.Equal("http://chat.test/v1", result.ApiBase);
    }

    [Fact]
    public async Task Test_WithNoEndpoint_SaysSoWithoutCallingAnything()
    {
        using var db = TestDb.Create();
        var h = Build(db);

        var result = await h.Controller.Test();

        Assert.False(result.Ok);
        Assert.Equal(0, h.Client.Calls);
    }
}
