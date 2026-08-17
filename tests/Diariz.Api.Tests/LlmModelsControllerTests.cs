using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>CRUD, validation and the delete guard. The <c>ManagePlatform</c> policy gate is NOT tested here:
/// this harness constructs the controller directly, so an <c>[Authorize(Policy=...)]</c> attribute never
/// runs and any assertion about it would pass whether or not the attribute existed. It is proved over the
/// real pipeline in <c>LlmModelsAuthTests</c>, the way <c>PlatformWebhooksAuthTests</c> does for its
/// sibling controller.</summary>
public class LlmModelsControllerTests
{
    private static LlmModelsController Build(DiarizDbContext db) =>
        new(db, new FakeApiKeyProtector(),
            Options.Create(new SummarizationOptions { ApiBase = "http://env/v1", Model = "env-model" }))
        { ControllerContext = Http.Context(Guid.NewGuid()) };

    private static LlmModel Seed(DiarizDbContext db, string name = "m", string? key = "enc:secret")
    {
        var m = new LlmModel
        {
            Id = Guid.NewGuid(), Name = name, ApiBase = "http://llm/v1", ContextLength = 8192,
            ApiKeyEncrypted = key, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.LlmModels.Add(m);
        db.SaveChanges();
        return m;
    }

    private static LlmModelUpsert Upsert(
        string name = "m", string? key = null, Dictionary<string, string>? parameters = null) =>
        new(name, "http://llm/v1", key, 8192, parameters ?? []);

    [Fact]
    public async Task Never_returns_the_api_key_only_whether_one_is_set()
    {
        // Write-only, the same contract the per-user key had: a stored secret cannot leak back out.
        using var db = TestDb.Create();
        Seed(db, key: "enc:secret");

        var result = await Build(db).List();
        var dto = Assert.Single(Assert.IsType<List<LlmModelDto>>(result.Value));

        Assert.True(dto.HasApiKey);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public async Task Reports_no_key_for_a_model_that_has_none()
    {
        using var db = TestDb.Create();
        Seed(db, key: null);

        var result = await Build(db).List();

        Assert.False(Assert.Single(Assert.IsType<List<LlmModelDto>>(result.Value)).HasApiKey);
    }

    [Fact]
    public async Task Update_without_a_key_leaves_the_stored_key_unchanged()
    {
        // The UI cannot send back a key it was never given, so omitting it must mean "keep it".
        using var db = TestDb.Create();
        var model = Seed(db, key: "enc:secret");

        await Build(db).Update(model.Id, Upsert(key: null));

        Assert.Equal("enc:secret", (await db.LlmModels.AsNoTracking().SingleAsync()).ApiKeyEncrypted);
    }

    [Fact]
    public async Task Update_with_a_key_replaces_it()
    {
        using var db = TestDb.Create();
        var model = Seed(db, key: "enc:old");

        await Build(db).Update(model.Id, Upsert(key: "new"));

        Assert.Equal("enc:new", (await db.LlmModels.AsNoTracking().SingleAsync()).ApiKeyEncrypted);
    }

    [Fact]
    public async Task Update_with_an_empty_key_clears_it()
    {
        // Distinct from null: a local endpoint that needs no key must be settable back to none.
        using var db = TestDb.Create();
        var model = Seed(db, key: "enc:old");

        await Build(db).Update(model.Id, Upsert(key: ""));

        Assert.Null((await db.LlmModels.AsNoTracking().SingleAsync()).ApiKeyEncrypted);
    }

    [Fact]
    public async Task Rejects_an_unknown_parameter_name()
    {
        // A typo must fail loudly. The layer merge ignores keys it does not recognise, so accepting this
        // would show the administrator a saved setting that silently does nothing.
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).Update(model.Id, Upsert(
            parameters: new Dictionary<string, string> { ["ModelBase"] = """{"temperatur":0.3}""" }));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("temperatur", bad.Value!.ToString());
    }

    [Fact]
    public async Task Rejects_an_unknown_group_name()
    {
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).Update(model.Id, Upsert(
            parameters: new Dictionary<string, string> { ["Sumaries"] = "{}" }));

        Assert.Contains("Sumaries", Assert.IsType<BadRequestObjectResult>(result.Result).Value!.ToString());
    }

    [Fact]
    public async Task Rejects_malformed_parameter_json()
    {
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).Update(model.Id, Upsert(
            parameters: new Dictionary<string, string> { ["ModelBase"] = "{not json" }));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Stores_an_explicit_null_so_a_group_can_omit_an_inherited_parameter()
    {
        // The tri-state the whole design rests on: absent inherits, null omits. If the API dropped nulls
        // there would be no way to say "do not send this for translation".
        using var db = TestDb.Create();
        var model = Seed(db);

        await Build(db).Update(model.Id, Upsert(
            parameters: new Dictionary<string, string> { ["Translation"] = """{"temperature":null}""" }));

        var row = await db.LlmModelParameters.AsNoTracking()
            .SingleAsync(p => p.Group == LlmCallGroup.Translation);
        Assert.Contains("temperature", row.ParametersJson);
        Assert.Contains("null", row.ParametersJson);
    }

    [Fact]
    public async Task Replaces_the_parameter_rows_it_is_given_and_removes_the_rest()
    {
        using var db = TestDb.Create();
        var model = Seed(db);
        await Build(db).Update(model.Id, Upsert(parameters: new Dictionary<string, string>
        {
            ["ModelBase"] = """{"temperature":0.5}""",
            ["Tags"] = """{"temperature":0.1}""",
        }));

        await Build(db).Update(model.Id, Upsert(parameters: new Dictionary<string, string>
        {
            ["ModelBase"] = """{"temperature":0.7}""",
        }));

        var rows = await db.LlmModelParameters.AsNoTracking().Where(p => p.LlmModelId == model.Id).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(LlmCallGroup.ModelBase, row.Group);
        Assert.Contains("0.7", row.ParametersJson);
    }

    [Fact]
    public async Task Rejects_a_duplicate_model_name()
    {
        using var db = TestDb.Create();
        Seed(db, name: "taken");

        var result = await Build(db).Create(Upsert(name: "taken"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Rejects_a_blank_name_or_endpoint()
    {
        using var db = TestDb.Create();

        Assert.IsType<BadRequestObjectResult>((await Build(db).Create(Upsert(name: " "))).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await Build(db).Create(new LlmModelUpsert("m", " ", null, 8192, []))).Result);
    }

    [Fact]
    public async Task Rejects_ModelBase_as_an_assignment_group()
    {
        // ModelBase is a parameter scope, not a call type - assigning a model to it is meaningless.
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).UpdateAssignments(new LlmAssignmentsDto(
            model.Id, new Dictionary<string, Guid> { ["ModelBase"] = model.Id }));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Saves_assignments_and_the_default_together()
    {
        using var db = TestDb.Create();
        var fast = Seed(db, name: "fast");
        var big = Seed(db, name: "big");

        await Build(db).UpdateAssignments(new LlmAssignmentsDto(
            big.Id, new Dictionary<string, Guid> { ["Tags"] = fast.Id }));

        var settings = await db.PlatformSettings.AsNoTracking().SingleAsync();
        Assert.Equal(big.Id, settings.DefaultLlmModelId);
        var assignment = Assert.Single(await db.LlmCallAssignments.AsNoTracking().ToListAsync());
        Assert.Equal(LlmCallGroup.Tags, assignment.Group);
        Assert.Equal(fast.Id, assignment.LlmModelId);
    }

    [Fact]
    public async Task Delete_of_an_assigned_model_is_refused_and_names_the_groups()
    {
        // The database cannot be relied on here: EF refuses at Remove for the assignment FK and silently
        // nulls the platform default for the other, so the guard has to live in the controller.
        using var db = TestDb.Create();
        var model = Seed(db);
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = model.Id });
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Tags, LlmModelId = model.Id });
        await db.SaveChangesAsync();

        var result = await Build(db).Delete(model.Id);

        var message = Assert.IsType<ConflictObjectResult>(result).Value!.ToString()!;
        Assert.Contains("Chat", message);
        Assert.Contains("Tags", message);
        Assert.Single(db.LlmModels);
    }

    [Fact]
    public async Task Delete_of_the_platform_default_is_refused()
    {
        // Postgres would NOT stop this one: the FK is nullable, so EF clears the default and lets the
        // delete through. Without this check the platform silently falls back to the environment model.
        using var db = TestDb.Create();
        var model = Seed(db);
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, DefaultLlmModelId = model.Id,
        });
        await db.SaveChangesAsync();

        var result = await Build(db).Delete(model.Id);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(db.LlmModels);
        Assert.Equal(model.Id, (await db.PlatformSettings.AsNoTracking().SingleAsync()).DefaultLlmModelId);
    }

    [Fact]
    public async Task Delete_removes_an_unused_model_and_its_parameters()
    {
        using var db = TestDb.Create();
        var model = Seed(db);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase, ParametersJson = "{}",
        });
        await db.SaveChangesAsync();

        var result = await Build(db).Delete(model.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.LlmModels);
        Assert.Empty(db.LlmModelParameters);
    }

    [Fact]
    public async Task Create_from_environment_persists_the_configured_endpoint_once()
    {
        using var db = TestDb.Create();

        var result = await Build(db).CreateFromEnvironment();

        var dto = Assert.IsType<LlmModelDto>(result.Value);
        Assert.Equal("env-model", dto.Name);
        Assert.Equal("http://env/v1", dto.ApiBase);
        Assert.Single(db.LlmModels);
    }

    [Fact]
    public async Task Create_from_environment_is_refused_once_any_model_exists()
    {
        // It is a one-time migration aid, not an import button - re-running it would resurrect a model an
        // administrator had deliberately deleted.
        using var db = TestDb.Create();
        Seed(db);

        var result = await Build(db).CreateFromEnvironment();

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Single(db.LlmModels);
    }
}
