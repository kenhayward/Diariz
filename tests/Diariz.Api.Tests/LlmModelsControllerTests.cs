using System.Text.Json;
using System.Text.Json.Nodes;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
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
    /// <summary>Every template resolves to its built-in default, which is what a deployment with no
    /// prompts/ volume gets.</summary>
    private sealed class FallbackTemplates : IPromptTemplateProvider
    {
        public string Get(string name, string fallback) => fallback;
    }

    private static LlmModelsController Build(
        DiarizDbContext db, LlmDefaultsOptions? defaults = null, ILlmTestProbe? probe = null) =>
        new(db, new FakeApiKeyProtector(),
            Options.Create(new SummarizationOptions { ApiBase = "http://env/v1", Model = "env-model" }),
            Options.Create(defaults ?? new LlmDefaultsOptions()),
            probe ?? new FakeLlmTestProbe(),
            new FakeLlmModelDiscoveryClient(),
            new LlmTestPromptFactory(db, new FallbackTemplates()))
        { ControllerContext = Http.Context(Guid.NewGuid()) };

    /// <summary>The same controller, but as a KNOWN administrator. The recording endpoints are scoped to the
    /// caller's own rows, so their tests cannot use the random id <see cref="Build"/> hands out.</summary>
    private static LlmModelsController BuildAs(
        DiarizDbContext db, Guid userId, ILlmTestProbe? probe = null) =>
        new(db, new FakeApiKeyProtector(),
            Options.Create(new SummarizationOptions { ApiBase = "http://env/v1", Model = "env-model" }),
            Options.Create(new LlmDefaultsOptions()),
            probe ?? new FakeLlmTestProbe(),
            new FakeLlmModelDiscoveryClient(),
            // A REAL factory over the same context, not a fake: these tests assert the controller wires the
            // real prompt through, and a fake would make every transcript assertion vacuous.
            new LlmTestPromptFactory(db, new FallbackTemplates()))
        { ControllerContext = Http.Context(userId) };

    private static Recording SeedRecording(DiarizDbContext db, Guid ownerId, string title = "Team sync")
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = ownerId, Title = title, Status = RecordingStatus.Transcribed,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Recordings.Add(rec);
        db.SaveChanges();
        return rec;
    }

    private static Recording SeedTranscribedRecording(DiarizDbContext db, Guid ownerId, string title = "Team sync")
    {
        var rec = SeedRecording(db, ownerId, title);
        var transcription = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Transcriptions.Add(transcription);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = transcription.Id, Ordinal = 0, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 3000, Original = "The Q3 forecast needs revising before Friday.",
        });
        db.SaveChanges();
        return rec;
    }

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

    [Fact]
    public async Task Defaults_exposes_the_app_default_layers_the_resolver_walks()
    {
        // The admin UI shows what a parameter resolves to when left on Inherit, and previews the request
        // body as it is typed. Both need the bottom of the layer stack, which lives in configuration and
        // which the browser has no other way to see.
        using var db = TestDb.Create();
        var controller = Build(db, new LlmDefaultsOptions
        {
            Temperature = 0.3,
            Translation = new LlmParameterDefaults { Temperature = 0.1 },
        });

        var layers = Assert.IsType<Dictionary<string, string>>(
            (await controller.Defaults()).Value);

        Assert.Equal(0.3, JsonNode.Parse(layers["ModelBase"])!["temperature"]!.GetValue<double>());
        Assert.Equal(0.1, JsonNode.Parse(layers["Translation"])!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public async Task Defaults_omits_a_group_that_configures_nothing()
    {
        // Absent has to stay distinguishable from "an empty layer": the UI walks these the same way the
        // resolver does, where a group with no layer inherits rather than deciding anything.
        using var db = TestDb.Create();
        var controller = Build(db, new LlmDefaultsOptions
        {
            Temperature = 0.3,
            Tags = new LlmParameterDefaults(),
        });

        var layers = Assert.IsType<Dictionary<string, string>>(
            (await controller.Defaults()).Value);

        Assert.False(layers.ContainsKey("Tags"));
        Assert.True(layers.ContainsKey("ModelBase"));
    }

    [Fact]
    public async Task Defaults_never_leaks_the_environment_api_key()
    {
        // It is a sibling of the endpoints that deliberately withhold the stored key, and it is reached by
        // the same page. Nothing here is a credential, and nothing here should become one.
        using var db = TestDb.Create();

        var layers = Assert.IsType<Dictionary<string, string>>(
            (await Build(db).Defaults()).Value);

        foreach (var (_, json) in layers)
            foreach (var (key, _) in JsonNode.Parse(json)!.AsObject())
                Assert.Contains(key, LlmParameterLayers.ParameterNames);
    }

    // ---- the administrator's test call ----

    /// <summary>A test call for the parameter-resolution cases below. The group is <b>Translation</b>, not
    /// a content group: those run the real prompt against a real recording, which these tests neither have
    /// nor care about. Translation is still a non-base group, so group-over-base layering is exercised
    /// exactly as before.</summary>
    private static LlmModelTestRequest TestReq(
        string group = "Translation", Dictionary<string, string>? parameters = null) =>
        new(group, parameters ?? []);

    [Fact]
    public async Task Test_call_uses_the_stored_endpoint_and_key_not_anything_the_caller_sent()
    {
        // The request body names a group and some parameters and NOTHING else. An endpoint that accepted a
        // caller-supplied URL would let an administrator's session be used to reach arbitrary hosts without
        // leaving a model row behind - and the row is the audit trail.
        using var db = TestDb.Create();
        var model = Seed(db, "m", key: "enc:the-secret");
        var probe = new FakeLlmTestProbe();

        await Build(db, probe: probe).Test(model.Id, TestReq());

        Assert.Equal("http://llm/v1", probe.LastConfig!.ApiBase);
        Assert.Equal("the-secret", probe.LastConfig.ApiKey);
        Assert.Equal("m", probe.LastConfig.Model);
    }

    [Fact]
    public async Task Test_call_resolves_the_unsaved_layers_the_admin_is_editing()
    {
        // The whole point of taking parameters in the body: an admin tests BEFORE saving, so what runs must
        // be what is on screen rather than what is in the database.
        using var db = TestDb.Create();
        var model = Seed(db);
        var probe = new FakeLlmTestProbe();
        var request = TestReq("Translation", new Dictionary<string, string>
        {
            ["ModelBase"] = "{\"temperature\":0.9,\"top_k\":40}",
            ["Translation"] = "{\"temperature\":0.2}",
        });

        await Build(db, probe: probe).Test(model.Id, request);

        // Group over base...
        Assert.Equal(0.2, probe.LastConfig!.Parameters.Temperature);
        // ...and the base still supplies what the group is silent about.
        Assert.Equal(40, probe.LastConfig.Parameters.TopK);
    }

    [Fact]
    public async Task Test_call_falls_through_to_the_application_defaults()
    {
        using var db = TestDb.Create();
        var model = Seed(db);
        var probe = new FakeLlmTestProbe();
        var defaults = new LlmDefaultsOptions
        {
            Temperature = 0.3,
            Translation = new LlmParameterDefaults { Temperature = 0.1 },
        };

        await Build(db, defaults, probe).Test(model.Id, TestReq("Translation"));

        // The group's application default beats the base one, exactly as LlmSettingsResolver walks them.
        Assert.Equal(0.1, probe.LastConfig!.Parameters.Temperature);
    }

    [Fact]
    public async Task Test_call_honours_an_omitted_parameter()
    {
        // null means "do not send this", and it has to survive the round trip through the request body -
        // otherwise the one thing an admin reaches for to fix a 400 cannot be tried before saving.
        using var db = TestDb.Create();
        var model = Seed(db);
        var probe = new FakeLlmTestProbe();
        var request = TestReq("Translation", new Dictionary<string, string>
        {
            ["ModelBase"] = "{\"top_k\":40}",
            ["Translation"] = "{\"top_k\":null}",
        });

        await Build(db, probe: probe).Test(model.Id, request);

        Assert.Null(probe.LastConfig!.Parameters.TopK);
    }

    [Fact]
    public async Task Test_call_returns_not_found_for_an_unknown_model()
    {
        using var db = TestDb.Create();

        var result = await Build(db).Test(Guid.NewGuid(), TestReq());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Test_call_rejects_a_group_that_is_not_a_call_group()
    {
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).Test(model.Id, TestReq("Nonsense"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Test_call_rejects_a_parameter_the_platform_cannot_send()
    {
        // The same guard the upsert applies. A key the layer merge ignores would produce a test that
        // silently proved nothing about the setting the admin thought they were testing.
        using var db = TestDb.Create();
        var model = Seed(db);
        var request = TestReq("Translation", new Dictionary<string, string>
        {
            ["Translation"] = "{\"temprature\":0.2}",
        });

        var result = await Build(db).Test(model.Id, request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Test_call_reports_what_the_probe_found()
    {
        using var db = TestDb.Create();
        var model = Seed(db);
        var outcome = new LlmTestOutcome(
            false, 400, null, 80, null, null, null, null, null, null, "{}", "Http400", "bad top_k", "top_k");

        var result = await Build(db, probe: new FakeLlmTestProbe(outcome)).Test(model.Id, TestReq());

        var dto = Assert.IsType<LlmTestOutcome>(result.Value);
        Assert.False(dto.Ok);
        Assert.Equal("top_k", dto.OffendingParameter);
    }

    [Fact]
    public async Task Test_call_is_attributed_to_the_administrator_who_ran_it()
    {
        // A call that cost tokens and left no trace in the usage log would be the one call an admin could
        // not account for. The ambient scope is what LlmTelemetryHandler reads to write that row.
        using var db = TestDb.Create();
        var model = Seed(db);
        var probe = new ScopeCapturingProbe();

        await Build(db, probe: probe).Test(model.Id, TestReq());

        Assert.Equal(LlmCallKind.AdminTest, probe.Kind);
    }

    [Fact]
    public async Task Test_call_records_which_administrator_ran_it()
    {
        // LlmCalls pairs the user FK with a denormalised email so a row stays readable after the account
        // goes. Leaving it blank puts every test call in one nameless bucket in the usage log's by-user
        // grouping, which reads as a real user who cannot be identified.
        using var db = TestDb.Create();
        var model = Seed(db);
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var probe = new ScopeCapturingProbe();
        var controller = Build(db, probe: probe);
        controller.ControllerContext = Http.Context(userId);

        await controller.Test(model.Id, TestReq());

        Assert.Equal(db.Users.Single(u => u.Id == userId).Email, probe.UserEmail);
    }

    // ---- Display name and the chat-offered flag ----

    [Fact]
    public async Task Round_trips_a_display_name()
    {
        using var db = TestDb.Create();
        var created = await Build(db).Create(Upsert("m") with { DisplayName = "QWEN 3.8" });

        var dto = Assert.IsType<LlmModelDto>(created.Value);
        Assert.Equal("QWEN 3.8", dto.DisplayName);
    }

    [Fact]
    public async Task Stores_a_blank_display_name_as_absent()
    {
        // One representation of "not set", so Label has a single thing to test rather than two spellings
        // of the same state.
        using var db = TestDb.Create();
        await Build(db).Create(Upsert("m") with { DisplayName = "   " });

        Assert.Null(db.LlmModels.Single().DisplayName);
    }

    [Fact]
    public async Task Toggling_chat_enabled_persists()
    {
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).SetChatEnabled(model.Id, new SetChatEnabledRequest(true));

        Assert.IsType<NoContentResult>(result);
        Assert.True(db.LlmModels.Single(m => m.Id == model.Id).ChatEnabled);
    }

    [Fact]
    public async Task Toggling_chat_enabled_on_a_missing_model_is_not_found()
    {
        using var db = TestDb.Create();
        Assert.IsType<NotFoundResult>(
            await Build(db).SetChatEnabled(Guid.NewGuid(), new SetChatEnabledRequest(true)));
    }

    [Fact]
    public async Task Saving_the_editor_does_not_reset_chat_enabled()
    {
        // The drawer never edits this flag, so an upsert must leave it alone. Were ChatEnabled part of
        // LlmModelUpsert, every save from the editor would post a stale value and silently un-offer the
        // model - and a setting that quietly reverts is worse than one that cannot be changed at all.
        using var db = TestDb.Create();
        var model = Seed(db);
        await Build(db).SetChatEnabled(model.Id, new SetChatEnabledRequest(true));

        await Build(db).Update(model.Id, Upsert("m") with { DisplayName = "Renamed" });

        Assert.True(db.LlmModels.Single(m => m.Id == model.Id).ChatEnabled);
    }

    [Fact]
    public async Task The_listing_reports_the_chat_flag()
    {
        using var db = TestDb.Create();
        var model = Seed(db);
        await Build(db).SetChatEnabled(model.Id, new SetChatEnabledRequest(true));

        var dto = Assert.Single(Assert.IsType<List<LlmModelDto>>((await Build(db).List()).Value));

        Assert.True(dto.ChatEnabled);
    }

    [Fact]
    public async Task Remembers_the_recording_an_administrator_chose()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = SeedRecording(db, userId, "Quarterly planning");

        await BuildAs(db, userId).SetTestRecording(new SetLlmTestRecordingRequest(rec.Id));
        var result = await BuildAs(db, userId).GetTestRecording();

        Assert.Equal(rec.Id, result.Value!.RecordingId);
        Assert.Equal("Quarterly planning", result.Value.Title);
    }

    [Fact]
    public async Task Forgets_a_recording_that_has_since_been_deleted()
    {
        // Nulled on READ rather than held by a foreign key: an admin's convenience setting must never be a
        // reason a user's recording cannot be deleted.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = userId, LlmTestRecordingId = Guid.NewGuid() });
        db.SaveChanges();

        var result = await BuildAs(db, userId).GetTestRecording();

        Assert.Null(result.Value!.RecordingId);
        Assert.Null(result.Value.Title);
    }

    [Fact]
    public async Task Refuses_to_remember_another_users_recording()
    {
        using var db = TestDb.Create();
        var admin = Guid.NewGuid();
        var someoneElse = SeedRecording(db, Guid.NewGuid());

        var response = await BuildAs(db, admin).SetTestRecording(new SetLlmTestRecordingRequest(someoneElse.Id));

        Assert.IsType<NotFoundObjectResult>(response);
        Assert.Null(db.UserSettings.FirstOrDefault(s => s.UserId == admin)?.LlmTestRecordingId);
    }

    [Fact]
    public async Task Clears_the_choice_when_given_nothing()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = SeedRecording(db, userId);
        await BuildAs(db, userId).SetTestRecording(new SetLlmTestRecordingRequest(rec.Id));

        await BuildAs(db, userId).SetTestRecording(new SetLlmTestRecordingRequest(null));

        Assert.Null((await BuildAs(db, userId).GetTestRecording()).Value!.RecordingId);
    }

    [Fact]
    public async Task Reports_nothing_for_an_administrator_with_no_settings_row_at_all()
    {
        using var db = TestDb.Create();

        var result = await BuildAs(db, Guid.NewGuid()).GetTestRecording();

        Assert.Null(result.Value!.RecordingId);
    }

    [Fact]
    public async Task Runs_the_real_tags_prompt_against_the_chosen_recording()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId);
        var probe = new FakeLlmTestProbe();

        await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Tags", [], rec.Id));

        // The real transcript reached the model, not the built-in sample's Priya/Sam excerpt.
        Assert.Contains("Q3 forecast", probe.LastMessages![1].Content);
        Assert.DoesNotContain("vendor review", probe.LastMessages[1].Content);
    }

    [Fact]
    public async Task Still_uses_the_built_in_sample_for_a_group_with_no_real_prompt()
    {
        // Chat needs a question and MinutesAndFormulas is a multi-call pipeline; both keep the old behaviour,
        // and neither may demand a recording.
        using var db = TestDb.Create();
        var model = Seed(db);
        var probe = new FakeLlmTestProbe();

        var result = await BuildAs(db, Guid.NewGuid(), probe).Test(model.Id, new LlmModelTestRequest("Chat", []));

        Assert.NotNull(result.Value);
        Assert.Contains("Priya", probe.LastMessages![1].Content);
        Assert.Equal(LlmTestSample.MaxResponseChars, probe.LastMaxResponseChars);
    }

    [Fact]
    public async Task Rejects_a_content_group_with_no_recording()
    {
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await BuildAs(db, Guid.NewGuid()).Test(model.Id, new LlmModelTestRequest("Summaries", []));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Rejects_another_users_recording()
    {
        using var db = TestDb.Create();
        var model = Seed(db);
        var someoneElse = SeedTranscribedRecording(db, Guid.NewGuid());

        var result = await BuildAs(db, Guid.NewGuid())
            .Test(model.Id, new LlmModelTestRequest("Tags", [], someoneElse.Id));

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Rejects_a_recording_with_no_transcript()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedRecording(db, userId); // no transcription, no segments

        var result = await BuildAs(db, userId).Test(model.Id, new LlmModelTestRequest("Tags", [], rec.Id));

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Parses_the_reply_the_way_the_pipeline_would()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId);
        var probe = new FakeLlmTestProbe(new LlmTestOutcome(
            true, 200, 10, 100, 1, 2, null, 3, "stop",
            """[{"tag":"Forecast","weight":0.8}]""", "{}", null, null, null));

        var result = await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Tags", [], rec.Id));

        Assert.Equal("Tags", result.Value!.ParsedKind);
        Assert.Equal("Forecast", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ExtractedTag>>(result.Value.Parsed)).Tag);
    }

    [Fact]
    public async Task Reports_an_empty_extraction_rather_than_failing()
    {
        // The parsers are total. A model that ignores the format leaves the pipeline with nothing, and that
        // is exactly what the administrator needs to see - not a 500, and not a success with no detail.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId);
        var probe = new FakeLlmTestProbe(new LlmTestOutcome(
            true, 200, 10, 100, 1, 2, null, 3, "stop",
            "I could not identify any topics.", "{}", null, null, null));

        var result = await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Tags", [], rec.Id));

        Assert.True(result.Value!.Ok);
        Assert.Equal("Tags", result.Value.ParsedKind);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ExtractedTag>>(result.Value.Parsed));
    }

    [Fact]
    public async Task Does_not_parse_a_failed_call()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId);
        var probe = new FakeLlmTestProbe(new LlmTestOutcome(
            false, 500, null, 100, null, null, null, null, null, null, "{}", "Http500", "boom", null));

        var result = await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Tags", [], rec.Id));

        Assert.Null(result.Value!.Parsed);
    }

    /// <summary>Serializes an outcome exactly as ASP.NET serializes a controller response, so an assertion
    /// about property names is about what the browser actually receives.</summary>
    private static JsonElement AsTheBrowserSeesIt(LlmTestOutcome outcome)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonConfig.Apply(options);
        return JsonDocument.Parse(JsonSerializer.Serialize(outcome, options)).RootElement.Clone();
    }

    [Fact]
    public async Task A_parsed_summary_reaches_the_browser_under_the_names_it_reads()
    {
        // Regression, 0.235.1. The parsed payload used to be hand-serialized into a STRING nested inside the
        // response. ASP.NET camelCases the envelope; a bare JsonSerializer.Serialize call does not. So the
        // outer object said "parsedJson" and the inner one said "Summary", and both looked right in
        // isolation - a perfectly good summary arrived and the panel reported "extracted no summary".
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId, "Named already");
        var probe = new FakeLlmTestProbe(new LlmTestOutcome(
            true, 200, 10, 100, 1, 2, null, 3, "stop",
            "The team agreed to revise the forecast before Friday.", "{}", null, null, null));

        var result = await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Summaries", [], rec.Id));
        var wire = AsTheBrowserSeesIt(result.Value!);

        var parsed = wire.GetProperty("parsed");
        Assert.Contains("revise the forecast", parsed.GetProperty("summary").GetString());
        Assert.True(parsed.TryGetProperty("name", out _), $"Got: {parsed}");
    }

    [Fact]
    public async Task Parsed_tags_reach_the_browser_under_the_names_it_reads()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId);
        var probe = new FakeLlmTestProbe(new LlmTestOutcome(
            true, 200, 10, 100, 1, 2, null, 3, "stop",
            """[{"tag":"Forecast","weight":0.8}]""", "{}", null, null, null));

        var result = await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Tags", [], rec.Id));
        var first = AsTheBrowserSeesIt(result.Value!).GetProperty("parsed")[0];

        Assert.Equal("Forecast", first.GetProperty("tag").GetString());
        Assert.Equal(0.8, first.GetProperty("weight").GetDouble());
    }

    [Fact]
    public async Task Parsed_actions_reach_the_browser_under_the_names_it_reads()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId);
        var probe = new FakeLlmTestProbe(new LlmTestOutcome(
            true, 200, 10, 100, 1, 2, null, 3, "stop",
            """[{"action":"Send the deck","actor":"Sam","deadline":"2026-09-01"}]""", "{}", null, null, null));

        var result = await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Actions", [], rec.Id));
        var first = AsTheBrowserSeesIt(result.Value!).GetProperty("parsed")[0];

        Assert.Equal("Send the deck", first.GetProperty("text").GetString());
        Assert.Equal("Sam", first.GetProperty("actor").GetString());
        Assert.Equal("2026-09-01", first.GetProperty("deadline").GetString());
    }

    [Fact]
    public async Task An_empty_extraction_reaches_the_browser_as_an_empty_array()
    {
        // The state the whole panel is for: a call that succeeded and would still have stored nothing. It
        // must arrive as [] - not null, which the browser cannot distinguish from "this group does not parse".
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var model = Seed(db);
        var rec = SeedTranscribedRecording(db, userId);
        var probe = new FakeLlmTestProbe(new LlmTestOutcome(
            true, 200, 10, 100, 1, 2, null, 3, "stop",
            "I could not identify any topics.", "{}", null, null, null));

        var result = await BuildAs(db, userId, probe).Test(model.Id, new LlmModelTestRequest("Tags", [], rec.Id));
        var parsed = AsTheBrowserSeesIt(result.Value!).GetProperty("parsed");

        Assert.Equal(JsonValueKind.Array, parsed.ValueKind);
        Assert.Equal(0, parsed.GetArrayLength());
    }

    [Fact]
    public async Task The_test_call_uses_the_same_timeout_the_real_call_would()
    {
        // The panel's whole purpose is to show what a real call does. It read only the application
        // defaults, so an administrator who raised the platform timeout to 600 saw a test give up at 120 -
        // and could not tell whether the endpoint or the setting was at fault (0.235.1).
        using var db = TestDb.Create();
        var model = Seed(db);
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 600,
        });
        db.SaveChanges();
        var probe = new FakeLlmTestProbe();

        await BuildAs(db, Guid.NewGuid(), probe).Test(model.Id, new LlmModelTestRequest("Chat", []));

        Assert.Equal(600, probe.LastConfig!.TimeoutSeconds);
    }

    [Fact]
    public async Task An_unsaved_timeout_still_beats_the_platform_one_in_a_test_call()
    {
        // Testing a change before saving is what the endpoint takes parameters for; the platform value is
        // the floor beneath them, not an override.
        using var db = TestDb.Create();
        var model = Seed(db);
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 600,
        });
        db.SaveChanges();
        var probe = new FakeLlmTestProbe();
        var request = TestReq("Translation", new Dictionary<string, string>
        {
            ["ModelBase"] = "{\"timeout_seconds\":30}",
        });

        await BuildAs(db, Guid.NewGuid(), probe).Test(model.Id, request);

        Assert.Equal(30, probe.LastConfig!.TimeoutSeconds);
    }

    [Fact]
    public async Task The_defaults_endpoint_reports_the_administrators_platform_timeout()
    {
        // The drawer resolves the inherited value and the request preview CLIENT-side, from this endpoint.
        // If it reported the shipped 120 while the server used 600, the panel would state a timeout no call
        // ever uses - the same two-derivations trap that produced the parsed-result bug (0.235.1).
        using var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = 600,
        });
        db.SaveChanges();

        var layers = (await Build(db).Defaults()).Value!;

        using var modelBase = JsonDocument.Parse(layers["ModelBase"]);
        Assert.Equal(600, modelBase.RootElement.GetProperty("timeout_seconds").GetInt32());
    }

    [Fact]
    public async Task The_defaults_endpoint_keeps_the_configured_timeout_when_the_admin_never_changed_it()
    {
        using var db = TestDb.Create();
        db.PlatformSettings.Add(new PlatformSettings
        {
            Id = PlatformSettings.SingletonId, LlmTimeoutSeconds = PlatformSettings.DefaultLlmTimeoutSeconds,
        });
        db.SaveChanges();
        var defaults = new LlmDefaultsOptions { TimeoutSeconds = 45 };

        var layers = (await Build(db, defaults).Defaults()).Value!;

        using var modelBase = JsonDocument.Parse(layers["ModelBase"]);
        Assert.Equal(45, modelBase.RootElement.GetProperty("timeout_seconds").GetInt32());
    }

    private sealed class ScopeCapturingProbe : ILlmTestProbe
    {
        public LlmCallKind? Kind { get; private set; }
        public string? UserEmail { get; private set; }

        public Task<LlmTestOutcome> RunAsync(
            LlmRequestConfig config, IReadOnlyList<ChatMessage> messages, int maxResponseChars,
            CancellationToken ct = default)
        {
            Kind = LlmCallScope.Active?.Kind;
            UserEmail = LlmCallScope.Active?.UserEmail;
            return Task.FromResult(new LlmTestOutcome(
                true, 200, 1, 2, null, null, null, null, null, "", "{}", null, null, null));
        }
    }
}
