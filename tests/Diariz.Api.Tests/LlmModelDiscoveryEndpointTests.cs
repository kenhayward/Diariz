using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>The two Add-all endpoints: what discovery reports, and what an import is allowed to create.
///
/// Its own file rather than more of <c>LlmModelsControllerTests</c>: that one is CRUD, validation and the
/// delete guard, and this is a separate concern that happens to hang off the same controller. The
/// <c>ManagePlatform</c> gate is not tested here for the same reason it is not tested there - the harness
/// constructs the controller directly, so the attribute never runs.</summary>
public class LlmModelDiscoveryEndpointTests
{
    private static LlmModelsController Build(DiarizDbContext db, ILlmModelDiscoveryClient discovery) =>
        new(db, new FakeApiKeyProtector(),
            Options.Create(new SummarizationOptions { ApiBase = "http://env/v1", Model = "env-model" }),
            Options.Create(new LlmDefaultsOptions()),
            new FakeLlmTestProbe(),
            discovery)
        { ControllerContext = Http.Context(Guid.NewGuid()) };

    private static FakeLlmModelDiscoveryClient Found(params DiscoveredModel[] models) =>
        new() { Models = [.. models] };

    private static void SeedExisting(DiarizDbContext db, string name)
    {
        db.LlmModels.Add(new LlmModel
        {
            Id = Guid.NewGuid(), Name = name, ApiBase = "http://existing/v1", ContextLength = 8192,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    // ---- Discovery ----

    [Fact]
    public async Task Marks_a_model_that_already_exists()
    {
        using var db = TestDb.Create();
        SeedExisting(db, "gpt-4o");
        var discovery = Found(
            new DiscoveredModel("gpt-4o", 128000, "llm"),
            new DiscoveredModel("llama-3.3-70b", null, null));

        var result = await Build(db, discovery).Discover(new DiscoverModelsRequest("http://lm.test/v1", null));
        var dtos = Assert.IsType<DiscoverModelsResultDto>(result.Value).Models;

        Assert.True(dtos.Single(d => d.Id == "gpt-4o").AlreadyExists);
        Assert.False(dtos.Single(d => d.Id == "llama-3.3-70b").AlreadyExists);
    }

    [Fact]
    public async Task Omits_non_chat_models()
    {
        using var db = TestDb.Create();
        var discovery = Found(
            new DiscoveredModel("gpt-4o", null, null),
            new DiscoveredModel("text-embedding-3-large", null, "embeddings"),
            new DiscoveredModel("whisper-1", null, null));

        var result = await Build(db, discovery).Discover(new DiscoverModelsRequest("http://lm.test/v1", null));

        Assert.Equal(["gpt-4o"], Assert.IsType<DiscoverModelsResultDto>(result.Value).Models.Select(d => d.Id));
    }

    [Fact]
    public async Task Defaults_an_unreported_context_length_and_says_so()
    {
        // The flag matters as much as the number: 16384 is a guess, and both the chat dial and the real
        // context budget read it. An administrator who cannot tell a guess from a measurement will not
        // correct it.
        using var db = TestDb.Create();
        var discovery = Found(
            new DiscoveredModel("gpt-4o", null, null),
            new DiscoveredModel("qwen", 200_000, "llm"));

        var result = await Build(db, discovery).Discover(new DiscoverModelsRequest("http://lm.test/v1", null));
        var dtos = Assert.IsType<DiscoverModelsResultDto>(result.Value).Models;

        var guessed = dtos.Single(d => d.Id == "gpt-4o");
        Assert.Equal(16384, guessed.ContextLength);
        Assert.False(guessed.ContextLengthReported);

        var known = dtos.Single(d => d.Id == "qwen");
        Assert.Equal(200_000, known.ContextLength);
        Assert.True(known.ContextLengthReported);
    }

    [Fact]
    public async Task Passes_the_endpoint_and_key_through_to_the_client()
    {
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        await Build(db, discovery).Discover(new DiscoverModelsRequest("  http://lm.test/v1  ", "sk-secret"));

        Assert.Equal("http://lm.test/v1", discovery.LastApiBase);
        Assert.Equal("sk-secret", discovery.LastApiKey);
    }

    [Fact]
    public async Task Refuses_a_blank_endpoint_without_calling_out()
    {
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        var result = await Build(db, discovery).Discover(new DiscoverModelsRequest("  ", null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, discovery.Calls);
    }

    [Fact]
    public async Task Reports_the_resolved_endpoint_rather_than_the_one_submitted()
    {
        // A server address typed without /v1 is corrected to the path that actually serves the models. The
        // dialog shows this, so the correction is visible instead of being applied behind the administrator.
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));
        discovery.ChatApiBase = "http://lm.test:1234/v1";

        var result = await Build(db, discovery)
            .Discover(new DiscoverModelsRequest("http://lm.test:1234", null));

        Assert.Equal("http://lm.test:1234/v1", Assert.IsType<DiscoverModelsResultDto>(result.Value).ApiBase);
    }

    [Fact]
    public async Task Refuses_a_server_that_serves_no_openai_endpoint()
    {
        // Reported live: models imported against a base with no /v1 were created happily and then every
        // chat call went to /chat/completions at the root, which LM Studio answers 200 and never streams -
        // so the reply simply never arrived. Refusing with the reason beats creating models that cannot answer.
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));
        discovery.ChatApiBase = null;

        var result = await Build(db, discovery)
            .Discover(new DiscoverModelsRequest("http://lm.test:1234", null));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("/v1", bad.Value!.ToString());
    }

    // ---- Import ----

    [Fact]
    public async Task Creates_only_the_requested_names_with_the_endpoint_and_key()
    {
        using var db = TestDb.Create();
        var discovery = Found(
            new DiscoveredModel("gpt-4o", 128000, "llm"),
            new DiscoveredModel("llama-3.3-70b", null, null));

        var result = await Build(db, discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", "sk-secret", ["gpt-4o"]));

        Assert.Equal(1, Assert.IsType<ImportModelsResultDto>(result.Value).Added);

        var created = Assert.Single(db.LlmModels);
        Assert.Equal("gpt-4o", created.Name);
        Assert.Equal("http://lm.test/v1", created.ApiBase);   // the resolved base, from the fake
        Assert.NotNull(created.ApiKeyEncrypted);
        Assert.Equal(128000, created.ContextLength);
    }

    [Fact]
    public async Task Defaults_the_context_length_it_was_not_told_and_names_it()
    {
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        var result = await Build(db, discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", null, ["gpt-4o"]));

        Assert.Equal(16384, Assert.Single(db.LlmModels).ContextLength);
        Assert.Equal(["gpt-4o"], Assert.IsType<ImportModelsResultDto>(result.Value).NeedContextLength);
    }

    [Fact]
    public async Task Skips_a_name_that_already_exists()
    {
        using var db = TestDb.Create();
        SeedExisting(db, "gpt-4o");
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        var result = await Build(db, discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", null, ["gpt-4o"]));
        var summary = Assert.IsType<ImportModelsResultDto>(result.Value);

        Assert.Equal(0, summary.Added);
        Assert.Equal(1, summary.Skipped);
        Assert.Single(db.LlmModels);
    }

    [Fact]
    public async Task Does_not_offer_the_new_models_in_chat()
    {
        // Importing forty models from a server must not put forty rows in everyone's chat picker. Offering
        // one is a separate, deliberate act.
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        await Build(db, discovery).Import(new ImportModelsRequest("http://lm.test/v1", null, ["gpt-4o"]));

        Assert.False(Assert.Single(db.LlmModels).ChatEnabled);
    }

    [Fact]
    public async Task Leaves_the_display_name_unset_so_the_slug_shows_through()
    {
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        await Build(db, discovery).Import(new ImportModelsRequest("http://lm.test/v1", null, ["gpt-4o"]));

        var created = Assert.Single(db.LlmModels);
        Assert.Null(created.DisplayName);
        Assert.Equal("gpt-4o", created.Label);
    }

    [Fact]
    public async Task Refuses_a_name_the_endpoint_did_not_report()
    {
        // The name list comes from the client, so it is caller input. Trusting it would let an administrator
        // session create a row for any model string against any endpoint, with discovery having never seen
        // it - which is exactly the audit trail the endpoint is supposed to leave.
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        var result = await Build(db, discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", null, ["not-on-this-server"]));

        Assert.Equal(0, Assert.IsType<ImportModelsResultDto>(result.Value).Added);
        Assert.Empty(db.LlmModels);
    }

    [Fact]
    public async Task Refuses_a_reported_model_that_is_not_a_chat_model()
    {
        // Discovery hides these, so a request naming one did not come from the dialog.
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("text-embedding-3-large", null, "embeddings"));

        var result = await Build(db, discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", null, ["text-embedding-3-large"]));

        Assert.Equal(0, Assert.IsType<ImportModelsResultDto>(result.Value).Added);
        Assert.Empty(db.LlmModels);
    }

    [Fact]
    public async Task Imports_several_models_in_one_call()
    {
        using var db = TestDb.Create();
        var discovery = Found(
            new DiscoveredModel("gpt-4o", 128000, "llm"),
            new DiscoveredModel("llama-3.3-70b", 131072, "llm"),
            new DiscoveredModel("qwen", 200_000, "llm"));

        var result = await Build(db, discovery)
            .Import(new ImportModelsRequest("http://lm.test/v1", null, ["gpt-4o", "qwen"]));

        Assert.Equal(2, Assert.IsType<ImportModelsResultDto>(result.Value).Added);
        Assert.Equal(["gpt-4o", "qwen"], db.LlmModels.Select(m => m.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Stores_the_resolved_endpoint_not_the_one_submitted()
    {
        // The defect itself: what gets written to ApiBase is what every future completion is posted to.
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));
        discovery.ChatApiBase = "http://lm.test:1234/v1";

        await Build(db, discovery).Import(new ImportModelsRequest("http://lm.test:1234", null, ["gpt-4o"]));

        Assert.Equal("http://lm.test:1234/v1", Assert.Single(db.LlmModels).ApiBase);
    }

    [Fact]
    public async Task Creates_nothing_when_no_endpoint_could_be_resolved()
    {
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));
        discovery.ChatApiBase = null;

        var result = await Build(db, discovery)
            .Import(new ImportModelsRequest("http://lm.test:1234", null, ["gpt-4o"]));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(db.LlmModels);
    }

    [Fact]
    public async Task Refuses_a_blank_endpoint()
    {
        using var db = TestDb.Create();
        var discovery = Found(new DiscoveredModel("gpt-4o", null, null));

        var result = await Build(db, discovery).Import(new ImportModelsRequest(" ", null, ["gpt-4o"]));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(db.LlmModels);
    }
}
