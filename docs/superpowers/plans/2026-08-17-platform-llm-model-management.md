# Platform LLM Model Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move every LLM model and sampling setting off individual users and out of hardcoded literals into per-model parameter sets with per-call-type overrides, administered from a new `/admin/llm-models` page and stored in the database.

**Architecture:** Three new tables (`LlmModel`, `LlmModelParameters`, `LlmCallAssignment`) hold model connections and parameter sets encoded as `jsonb`, where an absent key inherits and a `null` key means "send nothing". A four-layer resolver (call-group override, model base, group app default, base app default) produces a fully-decided `LlmParameters` record, which a single serialisation helper writes into each client's request body. App defaults live in configuration and are env-overridable, chosen so that an empty database reproduces today's request bodies exactly.

**Tech Stack:** .NET 10 + EF Core + Postgres (`jsonb`), xUnit with the in-memory provider for units and Testcontainers for integration, React 19 + TypeScript + vitest.

Spec: `docs/superpowers/specs/2026-08-17-platform-llm-model-management-design.md` - **read it before starting.** Every design decision below has a recorded reason there, including the alternatives that were rejected.

## Global Constraints

- **TDD is required.** Write the failing test, run it, watch it fail with the expected message, then write the minimal code. No production code without a preceding failing test.
- **Mutation-verify every assertion.** Break the thing, confirm *that specific test* fails with a real message. Assertions that check for **absence** (an omitted parameter, a refused delete, a dropped column) are the ones most likely to pass vacuously - give them explicit attention.
- **Behaviour preservation is the contract.** With an empty database and no env overrides, every request body must be byte-identical to today's. Task 1 pins this before anything moves; it must stay green through Tasks 6-8.
- **No em or en dashes** (`-` only) in UI strings, i18n catalogues, release notes, help articles, and user-visible copy. Code, comments and internal docs are unaffected.
- **`LlmCallKind` and `LlmCallGroup` are append-only** - never renumber. Ints in Postgres, same rule as `RecordingSource`.
- **No `InternalsVisibleTo`.** Tests reach internal behaviour through public seams and the fakes in `Diariz.Api.TestSupport`. No mocking library - add a fake rather than reaching for one.
- **`dotnet test --filter "Name=X"` does not work in this repo** despite CLAUDE.md. Use `--filter "FullyQualifiedName~X"`.
- **Build `Diariz.slnx` before pushing.** Unit-only runs miss integration and CodeQL compile breaks; controller constructor changes have a second construction site in `RbacIntegrationTests.cs`.
- **Npgsql rejects a non-zero-offset `DateTimeOffset` for `timestamptz`** and throws at `SaveChanges`. Call `.ToUniversalTime()` before storing. The in-memory provider will not catch this.
- **Never `git add -A`.** Stage explicit paths.
- **Branch:** `feat/platform-llm-models` (already created, spec already committed there). `main` is protected; finish by pushing and opening a PR.
- **Version:** `0.220.0` -> `0.221.0` (functional enhancement).

## File Structure

| File | Responsibility |
|---|---|
| `src/Diariz.Domain/Entities/LlmCallGroup.cs` | **Create.** The 7-member enum, `ModelBase = 0` |
| `src/Diariz.Domain/Entities/LlmModel.cs` | **Create.** Model identity + connection + context length |
| `src/Diariz.Domain/Entities/LlmModelParameters.cs` | **Create.** One parameter set per (model, group) |
| `src/Diariz.Domain/Entities/LlmCallAssignment.cs` | **Create.** Call group to model routing |
| `src/Diariz.Api/Services/Llm/LlmCallGroups.cs` | **Create.** `GroupFor(LlmCallKind)` - the exhaustive pure mapping |
| `src/Diariz.Api/Services/Llm/LlmParameters.cs` | **Create.** The fully-resolved value record |
| `src/Diariz.Api/Services/Llm/LlmParameterLayers.cs` | **Create.** The tri-state layer merge. Pure, no EF, no HTTP |
| `src/Diariz.Api/Services/Llm/LlmRequestBody.cs` | **Create.** Writes wire parameters into a request body. One place, so 7 clients each change by one line |
| `src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs` | **Create.** Replaces `SummarizationSettingsResolver`. Model choice + layer walk + env fallback |
| `src/Diariz.Api/Configuration/LlmDefaultsOptions.cs` | **Create.** Group-capable app defaults, env-bindable |
| `src/Diariz.Api/Controllers/LlmModelsController.cs` | **Create.** Platform-admin CRUD + assignments + create-from-environment |
| `apps/web/src/pages/LlmModels.tsx` | **Create.** The admin page, with its own Platform Administrator gate |
| `apps/web/src/components/llmmodels/ModelList.tsx` | **Create.** List, add, edit, delete |
| `apps/web/src/components/llmmodels/ModelEditorModal.tsx` | **Create.** Identity fields, 7 panels, copy control |
| `apps/web/src/components/llmmodels/ParameterPanel.tsx` | **Create.** One scope's fields, generated from the schema |
| `apps/web/src/components/llmmodels/ParameterField.tsx` | **Create.** The tri-state control: Inherit / Off / value |
| `apps/web/src/components/llmmodels/parameterSchema.ts` | **Create.** The 13 parameters, types and ranges - the single source panels and validation both read |
| The 7 chat clients | **Modify.** One line each, via `LlmRequestBody.Apply` |
| `src/Diariz.Api/Services/EmbeddingSettingsResolver.cs` | **Modify.** Endpoint fallback stops borrowing the user's |
| `src/Diariz.Domain/Entities/UserSettings.cs` | **Modify.** Seven columns removed |
| `apps/web/src/components/assistant/ModelDialog.tsx` | **Delete.** |

---

### Task 1: Characterisation tests for every client request body

**Nothing in this task changes production code.** It captures what the clients send *today*, so that Tasks 6-8 can prove they did not change it. This is also the test gap that motivated the whole change: no assertion on any request body exists.

**Files:**
- Create: `tests/Diariz.Api.TestSupport/CapturingHandler.cs`
- Test: `tests/Diariz.Api.Tests/Llm/RequestBodyCharacterisationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `CapturingHandler` - a `DelegatingHandler` exposing `LastBodyJson` as a `JsonElement` and `LastRequestUri`. Used by Tasks 7 and 8.

- [ ] **Step 1: Write the capturing handler**

Create `tests/Diariz.Api.TestSupport/CapturingHandler.cs`:

```csharp
using System.Net;
using System.Text.Json;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Captures the JSON body of the last request and returns a canned response. The only way to
/// assert what a client actually put on the wire - the clients build their bodies internally, so there is
/// no seam above HttpClient to inspect.</summary>
public sealed class CapturingHandler : DelegatingHandler
{
    private readonly string _responseJson;
    private readonly HttpStatusCode _status;

    public CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseJson = responseJson;
        _status = status;
        InnerHandler = new NoopHandler();
    }

    public string? LastBodyRaw { get; private set; }
    public Uri? LastRequestUri { get; private set; }

    /// <summary>The captured body parsed as JSON. Throws if nothing was captured, which is itself a useful
    /// failure - a test that asserts on the body when no request was made should not quietly pass.</summary>
    public JsonElement LastBody =>
        JsonDocument.Parse(LastBodyRaw ?? throw new InvalidOperationException("no request captured")).RootElement;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        if (request.Content is not null)
            LastBodyRaw = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) =>
            throw new NotSupportedException("CapturingHandler never forwards");
    }
}
```

- [ ] **Step 2: Write the characterisation tests**

Create `tests/Diariz.Api.Tests/Llm/RequestBodyCharacterisationTests.cs`. These assert **today's** behaviour, before any change:

```csharp
using System.Net.Http;
using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Xunit;

namespace Diariz.Api.Tests.Llm;

/// <summary>What each client actually puts on the wire. Written BEFORE the platform-parameter refactor so
/// it can prove the refactor changed nothing: with an empty database and no env overrides, every body here
/// must stay byte-identical.
///
/// Nothing asserted these before - the temperature literals could have changed and CI would have stayed
/// green.</summary>
public class RequestBodyCharacterisationTests
{
    private const string ChatResponse =
        """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    private static SummarizationRequestConfig Config() =>
        new("http://llm.test/v1", "k", "test-model", 120);

    private static (T Client, CapturingHandler Handler) Build<T>(Func<HttpClient, T> make)
    {
        var handler = new CapturingHandler(ChatResponse);
        return (make(new HttpClient(handler)), handler);
    }

    private static IReadOnlyList<SegmentDto> Segments() =>
        [new SegmentDto { Speaker = "SPEAKER_00", Text = "hello", StartMs = 0, EndMs = 1000 }];

    [Fact]
    public async Task Summarization_sends_model_temperature_and_messages_only()
    {
        var (client, handler) = Build(h => new SummarizationClient(h));
        await client.SummarizeAsync(Config(), Segments(), needName: false, template: "T");

        var body = handler.LastBody;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(0.3, body.GetProperty("temperature").GetDouble(), 3);
        Assert.True(body.TryGetProperty("messages", out _));

        // The absences matter as much as the presences: no token cap is sent, so output length is entirely
        // the server's choice. If a later change starts sending one, this must be a deliberate edit here.
        foreach (var absent in new[]
                 { "max_tokens", "max_completion_tokens", "top_p", "top_k", "repeat_penalty",
                   "frequency_penalty", "presence_penalty", "reasoning_effort" })
            Assert.False(body.TryGetProperty(absent, out _), $"unexpected {absent} in the request body");
    }

    [Fact]
    public async Task Translation_sends_a_lower_temperature_than_everything_else()
    {
        var (client, handler) = Build(h => new TranslationClient(h));
        await client.TranslateAsync(Config(), "hola", "en");

        Assert.Equal(0.1, handler.LastBody.GetProperty("temperature").GetDouble(), 3);
    }

    [Fact]
    public async Task Reasoning_effort_is_sent_only_when_the_config_supplies_one()
    {
        var (client, handler) = Build(h => new SummarizationClient(h));

        await client.SummarizeAsync(Config(), Segments(), false, "T");
        Assert.False(handler.LastBody.TryGetProperty("reasoning_effort", out _));

        await client.SummarizeAsync(Config() with { ReasoningEffort = "high" }, Segments(), false, "T");
        Assert.Equal("high", handler.LastBody.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Chat_stream_sends_stream_true_and_asks_for_usage()
    {
        var (client, handler) = Build(h => new ChatStreamClient(h));
        await foreach (var _ in client.StreamAsync(Config(), [new ChatMessage("user", "hi")])) { }

        var body = handler.LastBody;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(0.3, body.GetProperty("temperature").GetDouble(), 3);
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }
}
```

**Adjust the constructor and method calls to match the real signatures.** Read each client before writing its test - `TranslationClient.TranslateAsync`, `ChatStreamClient.StreamAsync` and the rest take the arguments they take, and this plan does not restate them. Add one `[Fact]` per client covering model, temperature, and the absence list: `SummarizationClient`, `MeetingMinutesClient`, `ActionsClient`, `TagsClient`, `TranslationClient`, and both `ChatStreamClient` methods.

- [ ] **Step 3: Run the tests**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RequestBodyCharacterisation"
```

Expected: PASS immediately. These describe existing behaviour, so they are green from the first run - that is correct for characterisation tests and is not a TDD violation.

- [ ] **Step 4: Mutation-verify**

Change `["temperature"] = 0.3` to `0.4` in `SummarizationClient.cs`. Re-run. Expected: the summarisation test FAILS with `Assert.Equal() Failure: 0.4 != 0.3`. Then change it back and confirm green. Repeat for `TranslationClient`'s `0.1`. **If these pass with the mutation in place the tests are worthless** - the most likely cause is that the client was constructed differently from how the test builds it, so the test never exercised the real path.

- [ ] **Step 5: Commit**

```bash
git add tests/Diariz.Api.TestSupport/CapturingHandler.cs tests/Diariz.Api.Tests/Llm/RequestBodyCharacterisationTests.cs
git commit -m "test(llm): pin every client's request body before the parameter refactor"
```

---

### Task 2: The call-group enum and its mapping

**Files:**
- Create: `src/Diariz.Domain/Entities/LlmCallGroup.cs`
- Create: `src/Diariz.Api/Services/Llm/LlmCallGroups.cs`
- Test: `tests/Diariz.Api.Tests/Llm/LlmCallGroupsTests.cs`

**Interfaces:**
- Produces: `LlmCallGroup` (enum) and `LlmCallGroups.GroupFor(LlmCallKind) -> LlmCallGroup?`, where null means "no sampling parameters apply". Used by Tasks 6 and 9.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Services.Llm;
using Diariz.Domain.Entities;
using Xunit;

namespace Diariz.Api.Tests.Llm;

public class LlmCallGroupsTests
{
    [Theory]
    [InlineData(LlmCallKind.Tags, LlmCallGroup.Tags)]
    [InlineData(LlmCallKind.ExtractActions, LlmCallGroup.Actions)]
    [InlineData(LlmCallKind.Summarize, LlmCallGroup.Summaries)]
    [InlineData(LlmCallKind.SectionSummary, LlmCallGroup.Summaries)]
    [InlineData(LlmCallKind.MeetingMinutes, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.SectionMinutes, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.MeetingTypeMinutes, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.FormulaRun, LlmCallGroup.MinutesAndFormulas)]
    [InlineData(LlmCallKind.Translation, LlmCallGroup.Translation)]
    [InlineData(LlmCallKind.ChatMessage, LlmCallGroup.Chat)]
    [InlineData(LlmCallKind.ChatTitle, LlmCallGroup.Chat)]
    public void Maps_each_chat_kind_to_its_group(LlmCallKind kind, LlmCallGroup expected) =>
        Assert.Equal(expected, LlmCallGroups.GroupFor(kind));

    [Theory]
    [InlineData(LlmCallKind.Embedding)]
    [InlineData(LlmCallKind.SearchQuery)]
    [InlineData(LlmCallKind.Dictation)]
    [InlineData(LlmCallKind.Unknown)]
    public void Has_no_group_for_kinds_that_send_no_sampling_parameters(LlmCallKind kind) =>
        Assert.Null(LlmCallGroups.GroupFor(kind));

    /// Enumerating the enum rather than listing cases: a new LlmCallKind added later without a decision
    /// would otherwise slip through both theories above, which only cover the members someone remembered.
    [Fact]
    public void Every_kind_is_accounted_for()
    {
        foreach (var kind in Enum.GetValues<LlmCallKind>())
            _ = LlmCallGroups.GroupFor(kind); // must not throw
    }

    [Fact]
    public void ModelBase_is_zero_so_it_can_be_a_non_nullable_unique_key_component() =>
        Assert.Equal(0, (int)LlmCallGroup.ModelBase);
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmCallGroups"
```

Expected: compile error - `LlmCallGroup` and `LlmCallGroups` do not exist.

- [ ] **Step 3: Write the enum**

`src/Diariz.Domain/Entities/LlmCallGroup.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>The scope a parameter set applies to. <see cref="ModelBase"/> is a real member rather than a
/// null marker because it forms half of a unique index with the model id, and Postgres treats NULLs as
/// distinct - a nullable "this is the base" column would silently permit two base rows per model.
///
/// Append only, never renumber - these are ints in Postgres, same rule as RecordingSource.</summary>
public enum LlmCallGroup
{
    ModelBase = 0,
    Tags = 1,
    Actions = 2,
    Summaries = 3,
    MinutesAndFormulas = 4,
    Translation = 5,
    Chat = 6,
}
```

- [ ] **Step 4: Write the mapping**

`src/Diariz.Api/Services/Llm/LlmCallGroups.cs`:

```csharp
using Diariz.Domain.Entities;

namespace Diariz.Api.Services.Llm;

public static class LlmCallGroups
{
    /// <summary>The parameter group whose settings apply to a call of this kind, or null for kinds that
    /// send no sampling parameters at all (embeddings post {model, input}, dictation posts multipart audio).
    ///
    /// Deliberately a switch with no default arm and no discard pattern: adding a member to LlmCallKind
    /// without deciding its group is then a COMPILE ERROR rather than a silent fallthrough to Chat, which
    /// would quietly apply the wrong model and parameters to a new call type.</summary>
    public static LlmCallGroup? GroupFor(LlmCallKind kind) => kind switch
    {
        LlmCallKind.Tags => LlmCallGroup.Tags,
        LlmCallKind.ExtractActions => LlmCallGroup.Actions,
        LlmCallKind.Summarize or LlmCallKind.SectionSummary => LlmCallGroup.Summaries,
        LlmCallKind.MeetingMinutes or LlmCallKind.SectionMinutes or LlmCallKind.MeetingTypeMinutes
            or LlmCallKind.FormulaRun => LlmCallGroup.MinutesAndFormulas,
        LlmCallKind.Translation => LlmCallGroup.Translation,
        LlmCallKind.ChatMessage or LlmCallKind.ChatTitle => LlmCallGroup.Chat,
        LlmCallKind.Embedding or LlmCallKind.SearchQuery or LlmCallKind.Dictation
            or LlmCallKind.Unknown => null,
    };
}
```

**Correction to the comment above:** C# does **not** enforce exhaustiveness over an enum, so an undecided member is *not* a compile error - and adding a `_ => throw` arm suppresses even the CS8509 warning. Include the throwing arm anyway (a loud failure beats a silent fallthrough to Chat), and rely on `Every_kind_is_accounted_for` enumerating the enum as the mechanism that actually catches it:

```csharp
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "no parameter group has been decided for this call kind"),
```

- [ ] **Step 5: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmCallGroups"
```

Expected: PASS.

- [ ] **Step 6: Mutation-verify that an undecided kind is caught**

Add a temporary member `TestKind = 99` to `LlmCallKind` and run the tests. Expected: `Every_kind_is_accounted_for` FAILS - the throwing default arm fires and the enumeration test reports it. It compiles fine, which is precisely why that test exists. Remove the member afterwards and confirm `git status` is clean, taking care not to change the file's line endings.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Domain/Entities/LlmCallGroup.cs src/Diariz.Api/Services/Llm/LlmCallGroups.cs tests/Diariz.Api.Tests/Llm/LlmCallGroupsTests.cs
git commit -m "feat(llm): add the call-group enum and its exhaustive mapping"
```

---

### Task 3: The parameter record and the tri-state layer merge

The heart of the change, and deliberately pure - no EF, no HTTP, no configuration. It can be exercised entirely in memory.

**Files:**
- Create: `src/Diariz.Api/Services/Llm/LlmParameters.cs`
- Create: `src/Diariz.Api/Services/Llm/LlmParameterLayers.cs`
- Test: `tests/Diariz.Api.Tests/Llm/LlmParameterLayersTests.cs`

**Interfaces:**
- Produces:
  - `LlmParameters` - a record of fully-decided values; a null property means "do not send".
  - `LlmParameterLayers.Resolve(IReadOnlyList<string?> layersMostSpecificFirst) -> LlmParameters`
  - `LlmParameterLayers.ParameterNames` - the 13 canonical JSON keys, for validation in Task 9 and the web schema in Task 10.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Services.Llm;
using Xunit;

namespace Diariz.Api.Tests.Llm;

/// <summary>The three states each parameter can be in, and how they compose across layers.
///
/// The distinction between "absent" and "null" is the whole point: with layered defaults, "I have not set
/// this here" and "do not send this at all" are different instructions, and a -1 sentinel could not express
/// both (-1 is a legal value for max_tokens and top_k on some servers).</summary>
public class LlmParameterLayersTests
{
    private static LlmParameters Resolve(params string?[] layers) => LlmParameterLayers.Resolve(layers);

    [Fact]
    public void A_value_in_the_most_specific_layer_wins()
    {
        var p = Resolve("""{"temperature":0.9}""", """{"temperature":0.3}""");
        Assert.Equal(0.9, p.Temperature);
    }

    [Fact]
    public void An_absent_key_inherits_from_the_next_layer_down()
    {
        var p = Resolve("""{"top_p":0.8}""", """{"temperature":0.3}""");
        Assert.Equal(0.3, p.Temperature);
        Assert.Equal(0.8, p.TopP);
    }

    [Fact]
    public void An_explicit_null_omits_the_parameter_and_stops_the_walk()
    {
        // The case a sentinel cannot express: a lower layer sets 0.3, this layer says send nothing at all.
        var p = Resolve("""{"temperature":null}""", """{"temperature":0.3}""");
        Assert.Null(p.Temperature);
    }

    [Fact]
    public void A_parameter_no_layer_mentions_is_not_sent()
    {
        var p = Resolve("""{"temperature":0.3}""");
        Assert.Null(p.TopK);
        Assert.Null(p.MaxTokens);
    }

    [Fact]
    public void Null_and_empty_layers_are_skipped_rather_than_treated_as_omissions()
    {
        // A model with no override row for a group must not thereby omit everything.
        var p = Resolve(null, "", "{}", """{"temperature":0.3}""");
        Assert.Equal(0.3, p.Temperature);
    }

    [Fact]
    public void Behaviour_flags_fall_back_to_their_documented_defaults_when_no_layer_sets_them()
    {
        // These are never "not sent" - they govern the client, so they always have a value.
        var p = Resolve("{}");
        Assert.False(p.ReasoningEnabled);
        Assert.True(p.ToolsSupported);
        Assert.False(p.ImagesSupported);
        Assert.Equal(LlmParameters.DefaultTimeoutSeconds, p.TimeoutSeconds);
    }

    [Fact]
    public void Reasoning_effort_is_free_text_so_a_model_specific_level_survives()
    {
        // qwen3 accepts xhigh; gpt-oss does not. An enum here would reject a legitimate value.
        var p = Resolve("""{"reasoning_effort":"xhigh"}""");
        Assert.Equal("xhigh", p.ReasoningEffort);
    }

    [Fact]
    public void Unknown_keys_are_ignored_rather_than_throwing()
    {
        // Forward compatibility: a row written by a newer build must not break an older one on rollback.
        var p = Resolve("""{"temperature":0.3,"nonsense":1}""");
        Assert.Equal(0.3, p.Temperature);
    }

    [Fact]
    public void Names_the_thirteen_parameters_exactly_once_each()
    {
        Assert.Equal(13, LlmParameterLayers.ParameterNames.Count);
        Assert.Equal(LlmParameterLayers.ParameterNames.Count,
            LlmParameterLayers.ParameterNames.Distinct().Count());
        Assert.Contains("temperature", LlmParameterLayers.ParameterNames);
        Assert.Contains("max_completion_tokens", LlmParameterLayers.ParameterNames);
        Assert.Contains("images_supported", LlmParameterLayers.ParameterNames);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmParameterLayers"
```

Expected: compile error - the types do not exist.

- [ ] **Step 3: Write the record**

`src/Diariz.Api/Services/Llm/LlmParameters.cs`:

```csharp
namespace Diariz.Api.Services.Llm;

/// <summary>A fully-resolved parameter set: every layer has been walked and every question answered.
///
/// A null on a WIRE parameter means "do not put this in the request body" - it is not a missing value to be
/// filled in later. The BEHAVIOUR flags are never null because they govern the client rather than the body,
/// so they always need an answer.</summary>
public sealed record LlmParameters
{
    public const int DefaultTimeoutSeconds = 120;

    // ---- wire parameters: null means the key is absent from the request body ----
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }
    public double? RepeatPenalty { get; init; }
    public double? FrequencyPenalty { get; init; }
    public double? PresencePenalty { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxCompletionTokens { get; init; }

    /// <summary>Free text, not an enum: gpt-oss takes low/medium/high, qwen3 also takes xhigh, and a future
    /// model will take something else. Only sent when <see cref="ReasoningEnabled"/> is true.</summary>
    public string? ReasoningEffort { get; init; }

    // ---- behaviour flags: never serialised, always decided ----
    public bool ReasoningEnabled { get; init; }
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    public bool ToolsSupported { get; init; } = true;
    public bool ImagesSupported { get; init; }
}
```

- [ ] **Step 4: Write the merge**

`src/Diariz.Api/Services/Llm/LlmParameterLayers.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diariz.Api.Services.Llm;

/// <summary>Merges parameter layers, most specific first, into one decided set.
///
/// Three states per key, and the last two are genuinely different instructions:
///   absent            -> keep looking in the next layer down
///   present, null     -> omit this parameter entirely; stop looking
///   present, a value  -> use it; stop looking
/// </summary>
public static class LlmParameterLayers
{
    public const string Temperature = "temperature";
    public const string TopP = "top_p";
    public const string TopK = "top_k";
    public const string RepeatPenalty = "repeat_penalty";
    public const string FrequencyPenalty = "frequency_penalty";
    public const string PresencePenalty = "presence_penalty";
    public const string MaxTokens = "max_tokens";
    public const string MaxCompletionTokens = "max_completion_tokens";
    public const string ReasoningEffort = "reasoning_effort";
    public const string ReasoningEnabled = "reasoning_enabled";
    public const string TimeoutSeconds = "timeout_seconds";
    public const string ToolsSupported = "tools_supported";
    public const string ImagesSupported = "images_supported";

    public static readonly IReadOnlyList<string> ParameterNames =
    [
        Temperature, TopP, TopK, RepeatPenalty, FrequencyPenalty, PresencePenalty,
        MaxTokens, MaxCompletionTokens, ReasoningEffort,
        ReasoningEnabled, TimeoutSeconds, ToolsSupported, ImagesSupported,
    ];

    public static LlmParameters Resolve(IReadOnlyList<string?> layersMostSpecificFirst)
    {
        var objects = new List<JsonObject>();
        foreach (var layer in layersMostSpecificFirst)
        {
            if (string.IsNullOrWhiteSpace(layer)) continue;
            // A malformed row must not take the platform down; treat it as "sets nothing" and move on.
            try
            {
                if (JsonNode.Parse(layer) is JsonObject o) objects.Add(o);
            }
            catch (JsonException) { /* ignore an unparseable layer */ }
        }

        return new LlmParameters
        {
            Temperature = Number(objects, Temperature),
            TopP = Number(objects, TopP),
            TopK = Integer(objects, TopK),
            RepeatPenalty = Number(objects, RepeatPenalty),
            FrequencyPenalty = Number(objects, FrequencyPenalty),
            PresencePenalty = Number(objects, PresencePenalty),
            MaxTokens = Integer(objects, MaxTokens),
            MaxCompletionTokens = Integer(objects, MaxCompletionTokens),
            ReasoningEffort = Text(objects, ReasoningEffort),
            ReasoningEnabled = Flag(objects, ReasoningEnabled) ?? false,
            TimeoutSeconds = Integer(objects, TimeoutSeconds) ?? LlmParameters.DefaultTimeoutSeconds,
            ToolsSupported = Flag(objects, ToolsSupported) ?? true,
            ImagesSupported = Flag(objects, ImagesSupported) ?? false,
        };
    }

    /// <summary>The first layer that mentions <paramref name="key"/> decides it. Returns null both when no
    /// layer mentions it and when the deciding layer says null - the caller cannot distinguish, and does
    /// not need to: both mean "no value".</summary>
    private static JsonNode? First(List<JsonObject> layers, string key, out bool found)
    {
        foreach (var layer in layers)
        {
            if (!layer.TryGetPropertyValue(key, out var node)) continue;
            found = true;
            return node;
        }
        found = false;
        return null;
    }

    private static double? Number(List<JsonObject> layers, string key)
    {
        var node = First(layers, key, out var found);
        if (!found || node is null) return null;
        return node.GetValue<double>();
    }

    private static int? Integer(List<JsonObject> layers, string key)
    {
        var node = First(layers, key, out var found);
        if (!found || node is null) return null;
        return node.GetValue<int>();
    }

    private static string? Text(List<JsonObject> layers, string key)
    {
        var node = First(layers, key, out var found);
        if (!found || node is null) return null;
        var s = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool? Flag(List<JsonObject> layers, string key)
    {
        var node = First(layers, key, out var found);
        if (!found || node is null) return null;
        return node.GetValue<bool>();
    }
}
```

- [ ] **Step 5: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmParameterLayers"
```

Expected: PASS, all 9 tests.

- [ ] **Step 6: Mutation-verify the absent/null distinction**

This is the assertion the whole design rests on. In `First`, change `if (!layer.TryGetPropertyValue(key, out var node)) continue;` to treat a null value as absent:

```csharp
if (!layer.TryGetPropertyValue(key, out var node) || node is null) continue;
```

Re-run. Expected: `An_explicit_null_omits_the_parameter_and_stops_the_walk` FAILS with `Assert.Null() Failure: 0.3`. Revert. If it still passes, the test is not exercising the layer walk.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Services/Llm/LlmParameters.cs src/Diariz.Api/Services/Llm/LlmParameterLayers.cs tests/Diariz.Api.Tests/Llm/LlmParameterLayersTests.cs
git commit -m "feat(llm): resolve layered parameters with absent/null/value semantics"
```

---

### Task 4: Request-body serialisation

One helper, so each of the 7 clients changes by exactly one line in Task 7.

**Files:**
- Create: `src/Diariz.Api/Services/Llm/LlmRequestBody.cs`
- Test: `tests/Diariz.Api.Tests/Llm/LlmRequestBodyTests.cs`

**Interfaces:**
- Consumes: `LlmParameters` (Task 3).
- Produces: `LlmRequestBody.Apply(IDictionary<string, object?> body, LlmParameters p)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Services.Llm;
using Xunit;

namespace Diariz.Api.Tests.Llm;

public class LlmRequestBodyTests
{
    private static Dictionary<string, object?> Apply(LlmParameters p)
    {
        var body = new Dictionary<string, object?> { ["model"] = "m" };
        LlmRequestBody.Apply(body, p);
        return body;
    }

    [Fact]
    public void Writes_each_wire_parameter_that_has_a_value()
    {
        var body = Apply(new LlmParameters
        {
            Temperature = 0.3, TopP = 0.9, TopK = 40, RepeatPenalty = 1.1,
            FrequencyPenalty = 0.2, PresencePenalty = 0.1, MaxTokens = 512, MaxCompletionTokens = 256,
        });

        Assert.Equal(0.3, body["temperature"]);
        Assert.Equal(0.9, body["top_p"]);
        Assert.Equal(40, body["top_k"]);
        Assert.Equal(1.1, body["repeat_penalty"]);
        Assert.Equal(0.2, body["frequency_penalty"]);
        Assert.Equal(0.1, body["presence_penalty"]);
        Assert.Equal(512, body["max_tokens"]);
        Assert.Equal(256, body["max_completion_tokens"]);
    }

    [Fact]
    public void Omits_a_null_parameter_entirely_rather_than_writing_a_json_null()
    {
        // Not a stylistic choice: sending "top_k": null to a server that validates types is a 400.
        var body = Apply(new LlmParameters { Temperature = 0.3 });

        Assert.False(body.ContainsKey("top_k"));
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body.ContainsKey("top_p"));
    }

    [Fact]
    public void Sends_reasoning_effort_only_when_reasoning_is_enabled()
    {
        Assert.False(Apply(new LlmParameters { ReasoningEnabled = false, ReasoningEffort = "high" })
            .ContainsKey("reasoning_effort"));

        Assert.Equal("high", Apply(new LlmParameters { ReasoningEnabled = true, ReasoningEffort = "high" })
            ["reasoning_effort"]);
    }

    [Fact]
    public void Never_writes_a_behaviour_flag_into_the_body()
    {
        // Timeout, tool support and image support govern the client. An endpoint receiving them would at
        // best ignore them and at worst reject the request.
        var body = Apply(new LlmParameters { TimeoutSeconds = 300, ToolsSupported = true, ImagesSupported = true });

        foreach (var key in new[] { "timeout_seconds", "tools_supported", "images_supported", "reasoning_enabled" })
            Assert.False(body.ContainsKey(key), $"{key} must never reach the wire");
    }

    [Fact]
    public void Leaves_keys_the_caller_already_set_alone()
    {
        var body = new Dictionary<string, object?> { ["model"] = "m", ["messages"] = new object() };
        LlmRequestBody.Apply(body, new LlmParameters { Temperature = 0.3 });

        Assert.Equal("m", body["model"]);
        Assert.True(body.ContainsKey("messages"));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmRequestBody"
```

Expected: compile error - `LlmRequestBody` does not exist.

- [ ] **Step 3: Write the helper**

```csharp
namespace Diariz.Api.Services.Llm;

/// <summary>Writes the wire parameters of a resolved set into an OpenAI-compatible request body.
///
/// One place, so every client sends the same shape and adding a parameter later is a single edit. A null
/// parameter is OMITTED - not written as a JSON null - because a server that validates types rejects
/// "top_k": null with a 400, and "do not send this" is exactly what a null here means.</summary>
public static class LlmRequestBody
{
    public static void Apply(IDictionary<string, object?> body, LlmParameters p)
    {
        Set(body, LlmParameterLayers.Temperature, p.Temperature);
        Set(body, LlmParameterLayers.TopP, p.TopP);
        Set(body, LlmParameterLayers.TopK, p.TopK);
        Set(body, LlmParameterLayers.RepeatPenalty, p.RepeatPenalty);
        Set(body, LlmParameterLayers.FrequencyPenalty, p.FrequencyPenalty);
        Set(body, LlmParameterLayers.PresencePenalty, p.PresencePenalty);
        Set(body, LlmParameterLayers.MaxTokens, p.MaxTokens);
        Set(body, LlmParameterLayers.MaxCompletionTokens, p.MaxCompletionTokens);

        // Gated on the flag so a non-reasoning endpoint never sees the field at all.
        if (p.ReasoningEnabled && p.ReasoningEffort is not null)
            body[LlmParameterLayers.ReasoningEffort] = p.ReasoningEffort;
    }

    private static void Set(IDictionary<string, object?> body, string key, object? value)
    {
        if (value is not null) body[key] = value;
    }
}
```

- [ ] **Step 4: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmRequestBody"
```

Expected: PASS.

- [ ] **Step 5: Mutation-verify the omission**

Change `Set` to `body[key] = value;` unconditionally. Re-run. Expected: `Omits_a_null_parameter_entirely` FAILS on `body.ContainsKey("top_k")`. Revert.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/Llm/LlmRequestBody.cs tests/Diariz.Api.Tests/Llm/LlmRequestBodyTests.cs
git commit -m "feat(llm): serialise resolved parameters, omitting the ones set to null"
```

---

### Task 5: App defaults, group-capable and env-overridable

**Files:**
- Create: `src/Diariz.Api/Configuration/LlmDefaultsOptions.cs`
- Modify: `src/Diariz.Api/Program.cs` (bind the section)
- Modify: `deploy/.env.example` (document the new variables)
- Test: `tests/Diariz.Api.Tests/Llm/LlmDefaultsOptionsTests.cs`

**Interfaces:**
- Consumes: `LlmParameterLayers.ParameterNames` (Task 3).
- Produces: `LlmDefaultsOptions` with `Section = "LlmDefaults"`, and `LlmDefaultsOptions.LayerFor(LlmCallGroup?) -> string?` returning that group's defaults as a JSON object string, plus `BaseLayer -> string?`. Used by Task 6.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Configuration;
using Diariz.Api.Services.Llm;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Diariz.Api.Tests.Llm;

public class LlmDefaultsOptionsTests
{
    private static LlmDefaultsOptions FromEnv(params (string Key, string Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
        var opts = new LlmDefaultsOptions();
        config.GetSection(LlmDefaultsOptions.Section).Bind(opts);
        return opts;
    }

    [Fact]
    public void Ships_todays_behaviour_as_its_defaults()
    {
        // The whole refactor rests on this: an empty database and no overrides must reproduce today.
        var p = LlmParameterLayers.Resolve(
            [null, null, new LlmDefaultsOptions().LayerFor(LlmCallGroup.Summaries), new LlmDefaultsOptions().BaseLayer]);

        Assert.Equal(0.3, p.Temperature);
        Assert.Null(p.MaxTokens);
        Assert.Null(p.TopP);
        Assert.False(p.ReasoningEnabled);
        Assert.Equal(120, p.TimeoutSeconds);
    }

    [Fact]
    public void Keeps_translations_lower_temperature_as_a_group_default()
    {
        var opts = new LlmDefaultsOptions();
        var p = LlmParameterLayers.Resolve(
            [null, null, opts.LayerFor(LlmCallGroup.Translation), opts.BaseLayer]);

        Assert.Equal(0.1, p.Temperature);
    }

    [Fact]
    public void Binds_a_base_value_from_configuration()
    {
        var opts = FromEnv(("LlmDefaults:Temperature", "0.7"));
        var p = LlmParameterLayers.Resolve([opts.BaseLayer]);

        Assert.Equal(0.7, p.Temperature);
    }

    [Fact]
    public void Binds_a_group_override_from_configuration()
    {
        var opts = FromEnv(("LlmDefaults:Temperature", "0.7"), ("LlmDefaults:Tags:Temperature", "0.0"));

        Assert.Equal(0.0, LlmParameterLayers.Resolve([opts.LayerFor(LlmCallGroup.Tags), opts.BaseLayer]).Temperature);
        Assert.Equal(0.7, LlmParameterLayers.Resolve([opts.LayerFor(LlmCallGroup.Chat), opts.BaseLayer]).Temperature);
    }

    [Fact]
    public void Has_no_layer_for_a_group_nobody_configured()
    {
        Assert.Null(FromEnv(("LlmDefaults:Temperature", "0.7")).LayerFor(LlmCallGroup.Chat));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmDefaultsOptions"
```

Expected: compile error.

- [ ] **Step 3: Write the options class**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Diariz.Domain.Entities;

namespace Diariz.Api.Configuration;

/// <summary>One group's worth of default parameters. Every property is nullable so that "not configured"
/// stays distinguishable from "configured to a value" - only what is set becomes a layer key.</summary>
public class LlmParameterDefaults
{
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? RepeatPenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public string? ReasoningEffort { get; set; }
    public bool? ReasoningEnabled { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool? ToolsSupported { get; set; }
    public bool? ImagesSupported { get; set; }

    /// <summary>This set as a parameter-layer JSON object, or null when nothing is configured. Only
    /// properties that were actually set appear, so an unset property inherits rather than omitting.</summary>
    public string? ToLayer()
    {
        var o = new JsonObject();
        Add(o, Services.Llm.LlmParameterLayers.Temperature, Temperature);
        Add(o, Services.Llm.LlmParameterLayers.TopP, TopP);
        Add(o, Services.Llm.LlmParameterLayers.TopK, TopK);
        Add(o, Services.Llm.LlmParameterLayers.RepeatPenalty, RepeatPenalty);
        Add(o, Services.Llm.LlmParameterLayers.FrequencyPenalty, FrequencyPenalty);
        Add(o, Services.Llm.LlmParameterLayers.PresencePenalty, PresencePenalty);
        Add(o, Services.Llm.LlmParameterLayers.MaxTokens, MaxTokens);
        Add(o, Services.Llm.LlmParameterLayers.MaxCompletionTokens, MaxCompletionTokens);
        if (!string.IsNullOrWhiteSpace(ReasoningEffort))
            o[Services.Llm.LlmParameterLayers.ReasoningEffort] = JsonValue.Create(ReasoningEffort);
        Add(o, Services.Llm.LlmParameterLayers.ReasoningEnabled, ReasoningEnabled);
        Add(o, Services.Llm.LlmParameterLayers.TimeoutSeconds, TimeoutSeconds);
        Add(o, Services.Llm.LlmParameterLayers.ToolsSupported, ToolsSupported);
        Add(o, Services.Llm.LlmParameterLayers.ImagesSupported, ImagesSupported);

        return o.Count == 0 ? null : o.ToJsonString();
    }

    private static void Add<T>(JsonObject o, string key, T? value) where T : struct
    {
        if (value.HasValue) o[key] = JsonValue.Create(value.Value);
    }
}

/// <summary>Application defaults for LLM parameters: a base set plus optional per-group overrides, each
/// field overridable from the environment (LlmDefaults__Temperature, LlmDefaults__Translation__Temperature).
///
/// The shipped values reproduce today's request bodies exactly, which is what makes the platform-parameter
/// change behaviour-preserving on an empty database.</summary>
public class LlmDefaultsOptions
{
    public const string Section = "LlmDefaults";

    public double? Temperature { get; set; } = 0.3;
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? RepeatPenalty { get; set; }
    public double? FrequencyPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public string? ReasoningEffort { get; set; } = "medium";
    public bool? ReasoningEnabled { get; set; } = false;
    public int? TimeoutSeconds { get; set; } = 120;
    public bool? ToolsSupported { get; set; } = true;
    public bool? ImagesSupported { get; set; } = false;

    /// <summary>Today's deliberate exception: translation runs cooler than everything else.</summary>
    public LlmParameterDefaults Translation { get; set; } = new() { Temperature = 0.1 };

    public LlmParameterDefaults Tags { get; set; } = new();
    public LlmParameterDefaults Actions { get; set; } = new();
    public LlmParameterDefaults Summaries { get; set; } = new();
    public LlmParameterDefaults MinutesAndFormulas { get; set; } = new();
    public LlmParameterDefaults Chat { get; set; } = new();

    public string? BaseLayer => new LlmParameterDefaults
    {
        Temperature = Temperature, TopP = TopP, TopK = TopK, RepeatPenalty = RepeatPenalty,
        FrequencyPenalty = FrequencyPenalty, PresencePenalty = PresencePenalty,
        MaxTokens = MaxTokens, MaxCompletionTokens = MaxCompletionTokens,
        ReasoningEffort = ReasoningEffort, ReasoningEnabled = ReasoningEnabled,
        TimeoutSeconds = TimeoutSeconds, ToolsSupported = ToolsSupported, ImagesSupported = ImagesSupported,
    }.ToLayer();

    public string? LayerFor(LlmCallGroup? group) => group switch
    {
        LlmCallGroup.Tags => Tags.ToLayer(),
        LlmCallGroup.Actions => Actions.ToLayer(),
        LlmCallGroup.Summaries => Summaries.ToLayer(),
        LlmCallGroup.MinutesAndFormulas => MinutesAndFormulas.ToLayer(),
        LlmCallGroup.Translation => Translation.ToLayer(),
        LlmCallGroup.Chat => Chat.ToLayer(),
        _ => null,
    };
}
```

- [ ] **Step 4: Bind the section**

In `Program.cs`, beside the other `Configure<...>` calls:

```csharp
builder.Services.Configure<LlmDefaultsOptions>(builder.Configuration.GetSection(LlmDefaultsOptions.Section));
```

- [ ] **Step 5: Document the environment variables**

Append to `deploy/.env.example`, matching the file's existing comment style:

```bash
# ---- LLM parameter defaults ----
# Applied when neither the model nor its call-group override sets a value. The shipped defaults reproduce
# the values that were hardcoded before 0.221.0, so leaving these unset changes nothing.
# Per-group overrides use a second segment: Tags, Actions, Summaries, MinutesAndFormulas, Translation, Chat.
#LlmDefaults__Temperature=0.3
#LlmDefaults__Translation__Temperature=0.1
#LlmDefaults__TimeoutSeconds=120
#LlmDefaults__MaxTokens=
#LlmDefaults__ReasoningEnabled=false
```

- [ ] **Step 6: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmDefaultsOptions"
```

Expected: PASS.

- [ ] **Step 7: Mutation-verify**

Change the shipped `Translation` default to `new()` (no temperature). Re-run. Expected: `Keeps_translations_lower_temperature_as_a_group_default` FAILS with `0.3 != 0.1`. Revert.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Configuration/LlmDefaultsOptions.cs src/Diariz.Api/Program.cs deploy/.env.example tests/Diariz.Api.Tests/Llm/LlmDefaultsOptionsTests.cs
git commit -m "feat(llm): add group-capable app defaults that reproduce today's parameters"
```

---

### Task 6: Domain entities and the migration

> **CORRECTED DURING EXECUTION - this task is ADDITIVE ONLY.** As originally written it also dropped the
> seven `UserSettings` columns, but their consumers are not removed until Tasks 7 and 11, so the branch
> would not build at the end of this task - violating the rule that every task ends independently
> testable. The compiler puts the blast radius at exactly four files: `ChatContextResolver.cs`,
> `EmbeddingSettingsResolver.cs`, `SummarizationSettingsResolver.cs` and `UserSettingsController.cs`.
> **The column drops and their migration now belong to Task 11**, alongside the code that reads them.

**Files:**
- Create: `src/Diariz.Domain/Entities/LlmModel.cs`, `LlmModelParameters.cs`, `LlmCallAssignment.cs`
- Modify: `src/Diariz.Domain/Entities/PlatformSettings.cs` (add `DefaultLlmModelId`, mark `LlmTimeoutSeconds` obsolete)
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Test: `tests/Diariz.Api.IntegrationTests/LlmModelSchemaTests.cs`

Two things to know before starting. The `OnModelCreating` parameter is named `builder`, not `modelBuilder`.
And `dotnet ef migrations add` will warn *"An operation was scaffolded that may result in the loss of
data"* - that is EF being conservative about the new nullable FK column; verify it by confirming `Up`
contains **zero** `DropColumn` calls, which it does.

**Interfaces:**
- Produces: the three entities and `DiarizDbContext.LlmModels`, `.LlmModelParameters`, `.LlmCallAssignments`. Used by Tasks 7 and 9.

**Background you need:** the house pattern for JSON columns is a `string` property mapped to `jsonb` **inside the `isNpgsql` guard** (see `ChatToolOverridesJson` and `MeetingType.ContentJson` in `DiarizDbContext`), so the in-memory provider stores plain text and unit tests still build the model. Do not put the entity itself behind the guard - only the `jsonb` column type.

- [ ] **Step 1: Write the failing integration test**

Unique indexes, FK restrict, and `jsonb` round-trips are all Postgres behaviour the in-memory provider does not model, so these belong in the integration project.

```csharp
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Diariz.Api.IntegrationTests;

[Collection("integration")]
public class LlmModelSchemaTests
{
    private readonly ContainersFixture _fx;
    public LlmModelSchemaTests(ContainersFixture fx) => _fx = fx;

    private static LlmModel NewModel(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, ApiBase = "http://llm.test/v1", ContextLength = 8192,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Round_trips_all_three_parameter_states_through_jsonb()
    {
        // Postgres reformats jsonb, so compare parsed values - never the raw text.
        await using var db = _fx.CreateDbContext();
        var model = NewModel($"m-{Guid.NewGuid():N}");
        db.LlmModels.Add(model);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase,
            ParametersJson = """{"temperature":0.5,"top_k":null}""",
        });
        await db.SaveChangesAsync();

        await using var read = _fx.CreateDbContext();
        var row = await read.LlmModelParameters.SingleAsync(p => p.LlmModelId == model.Id);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(row.ParametersJson)!.AsObject();

        Assert.Equal(0.5, parsed["temperature"]!.GetValue<double>());
        Assert.True(parsed.ContainsKey("top_k"));   // present...
        Assert.Null(parsed["top_k"]);               // ...and null: the omit instruction survived storage
    }

    [Fact]
    public async Task Refuses_two_base_rows_for_one_model()
    {
        // The reason Group is non-nullable with ModelBase = 0: Postgres treats NULLs as distinct, so a
        // nullable marker would let this through.
        await using var db = _fx.CreateDbContext();
        var model = NewModel($"m-{Guid.NewGuid():N}");
        db.LlmModels.Add(model);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase, ParametersJson = "{}",
        });
        await db.SaveChangesAsync();

        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase, ParametersJson = "{}",
        });
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Refuses_two_models_with_the_same_name()
    {
        await using var db = _fx.CreateDbContext();
        var name = $"m-{Guid.NewGuid():N}";
        db.LlmModels.Add(NewModel(name));
        await db.SaveChangesAsync();

        db.LlmModels.Add(NewModel(name));
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Refuses_to_delete_a_model_an_assignment_points_at()
    {
        await using var db = _fx.CreateDbContext();
        var model = NewModel($"m-{Guid.NewGuid():N}");
        db.LlmModels.Add(model);
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = model.Id });
        await db.SaveChangesAsync();

        db.LlmModels.Remove(model);
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deletes_a_models_parameter_rows_with_it()
    {
        await using var db = _fx.CreateDbContext();
        var model = NewModel($"m-{Guid.NewGuid():N}");
        db.LlmModels.Add(model);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.Chat, ParametersJson = "{}",
        });
        await db.SaveChangesAsync();

        db.LlmModels.Remove(model);
        await db.SaveChangesAsync();

        await using var read = _fx.CreateDbContext();
        Assert.Empty(await read.LlmModelParameters.Where(p => p.LlmModelId == model.Id).ToListAsync());
    }

    [Fact]
    public async Task The_user_settings_llm_columns_are_gone()
    {
        // Asserting the migration actually dropped them, not merely that the C# property is absent.
        await using var db = _fx.CreateDbContext();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            select column_name from information_schema.columns
            where table_name = 'UserSettings'
              and column_name in ('SummaryApiBase','SummaryApiKeyEncrypted','SummaryModel',
                                  'ChatContextWindow','LlmTimeoutSeconds','ReasoningEnabled','ReasoningEffort')
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var found = new List<string>();
        while (await reader.ReadAsync()) found.Add(reader.GetString(0));

        Assert.Empty(found);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmModelSchema"
```

Expected: compile error - the entities do not exist. Docker must be running.

- [ ] **Step 3: Write the entities**

`src/Diariz.Domain/Entities/LlmModel.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>One model the platform can call: its identity, how to reach it, and its context window.
/// Self-contained, so pointing a call group at a model brings the connection with it - which is what lets a
/// local LM Studio model and a cloud model coexist.</summary>
public class LlmModel
{
    public Guid Id { get; set; }

    /// <summary>The literal string sent as `model`, e.g. openai/gpt-oss-20b. Unique.</summary>
    public string Name { get; set; } = string.Empty;

    public string ApiBase { get; set; } = string.Empty;

    /// <summary>Encrypted at rest via IApiKeyProtector; never returned to clients. Null = no key needed.</summary>
    public string? ApiKeyEncrypted { get; set; }

    /// <summary>The model's context window in tokens. A fact about the model, which is why it lives here
    /// rather than in per-user settings where it used to be.</summary>
    public int ContextLength { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<LlmModelParameters> Parameters { get; set; } = new List<LlmModelParameters>();
}
```

`src/Diariz.Domain/Entities/LlmModelParameters.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>One scope's parameter set for a model: the model base (<see cref="LlmCallGroup.ModelBase"/>) or
/// one call group's override. Stored as a JSON object where an absent key inherits from the next layer down
/// and a key present with null means "send nothing".</summary>
public class LlmModelParameters
{
    public Guid Id { get; set; }
    public Guid LlmModelId { get; set; }
    public LlmModel? Model { get; set; }

    public LlmCallGroup Group { get; set; }

    /// <summary>jsonb on Postgres, plain text elsewhere.</summary>
    public string ParametersJson { get; set; } = "{}";
}
```

`src/Diariz.Domain/Entities/LlmCallAssignment.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Which model serves a call group. At most six rows; a group with no row uses
/// <see cref="PlatformSettings.DefaultLlmModelId"/>.</summary>
public class LlmCallAssignment
{
    /// <summary>Primary key. Never <see cref="LlmCallGroup.ModelBase"/> - validated on write.</summary>
    public LlmCallGroup Group { get; set; }

    public Guid LlmModelId { get; set; }
    public LlmModel? Model { get; set; }
}
```

- [ ] **Step 4: Modify PlatformSettings and UserSettings**

In `PlatformSettings.cs`, add:

```csharp
    /// <summary>The model used by any call group with no explicit assignment. Null falls through to the
    /// model synthesized from Summarization:ApiBase, so an upgrade with no rows keeps working.</summary>
    public Guid? DefaultLlmModelId { get; set; }
    public LlmModel? DefaultLlmModel { get; set; }
```

and replace the `LlmTimeoutSeconds` doc comment with:

```csharp
    /// <summary>OBSOLETE from 0.221.0: the timeout is now a parameter on a model's set. Kept because a
    /// migration cannot fold it into a model row - the endpoint lives in the environment, not the database -
    /// so dropping it would silently reset a tuned production timeout. Read only by the synthesized
    /// environment-fallback model, and unreachable once any LlmModel row exists. Removable in a later release.</summary>
    public int LlmTimeoutSeconds { get; set; } = DefaultLlmTimeoutSeconds;
```

In `UserSettings.cs`, delete these seven properties and their doc comments: `SummaryApiBase`, `SummaryApiKeyEncrypted`, `SummaryModel`, `ChatContextWindow`, `LlmTimeoutSeconds`, `ReasoningEnabled`, `ReasoningEffort`. Update the class summary, which currently says the type "holds the user's own OpenAI-compatible summarisation config".

- [ ] **Step 5: Configure the model**

In `DiarizDbContext.OnModelCreating`, add three `DbSet` properties and this configuration. The `jsonb` line goes **inside** the existing `isNpgsql` guard; everything else stays outside it so unit tests can build the model:

```csharp
        modelBuilder.Entity<LlmModel>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => m.Name).IsUnique();
            e.Property(m => m.Name).IsRequired();
            e.Property(m => m.ApiBase).IsRequired();
        });

        modelBuilder.Entity<LlmModelParameters>(e =>
        {
            e.HasKey(p => p.Id);
            // Group is non-nullable with ModelBase = 0 precisely so this index works: Postgres treats NULLs
            // as distinct, so a nullable "base" marker would permit two base rows per model.
            e.HasIndex(p => new { p.LlmModelId, p.Group }).IsUnique();
            e.HasOne(p => p.Model).WithMany(m => m.Parameters)
                .HasForeignKey(p => p.LlmModelId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LlmCallAssignment>(e =>
        {
            e.HasKey(a => a.Group);
            // Restrict, not SetNull: deleting a model that is in use should fail loudly with the groups
            // named, rather than silently re-routing those call types to the default model.
            e.HasOne(a => a.Model).WithMany()
                .HasForeignKey(a => a.LlmModelId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlatformSettings>()
            .HasOne(p => p.DefaultLlmModel).WithMany()
            .HasForeignKey(p => p.DefaultLlmModelId).OnDelete(DeleteBehavior.Restrict);
```

and inside the `isNpgsql` guard:

```csharp
            modelBuilder.Entity<LlmModelParameters>()
                .Property(p => p.ParametersJson).HasColumnType("jsonb");
```

- [ ] **Step 6: Create the migration**

```bash
dotnet ef migrations add PlatformLlmModels --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Read the generated migration before continuing. It must create the three tables, add `DefaultLlmModelId`, and **drop the seven `UserSettings` columns**. No `CurrentFormat` bump is needed - restore runs `pg_restore --clean` and then migrates up, so an older backup's columns restore and are then dropped by this migration.

- [ ] **Step 7: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmModelSchema"
```

Expected: PASS, all 6 tests.

- [ ] **Step 8: Mutation-verify the two constraints most likely to be fictional**

Change the unique index to `e.HasIndex(p => new { p.LlmModelId, p.Group });` (non-unique), regenerate the migration, re-run. Expected: `Refuses_two_base_rows_for_one_model` FAILS because no exception is thrown. Revert. Then change `DeleteBehavior.Restrict` to `Cascade` on `LlmCallAssignment` and confirm `Refuses_to_delete_a_model_an_assignment_points_at` FAILS. Revert and regenerate.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Domain src/Diariz.Api tests/Diariz.Api.IntegrationTests/LlmModelSchemaTests.cs
git commit -m "feat(llm): add the platform model tables and drop the per-user LLM columns"
```

---

### Task 7: The resolver

**Files:**
- Create: `src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs`
- Delete: `src/Diariz.Api/Services/SummarizationSettingsResolver.cs`
- Modify: every call site of `ISummarizationSettingsResolver` (mechanical)
- Test: `tests/Diariz.Api.Tests/Llm/LlmSettingsResolverTests.cs`

**Interfaces:**
- Consumes: `LlmCallGroups.GroupFor` (Task 2), `LlmParameterLayers.Resolve` (Task 3), `LlmDefaultsOptions` (Task 5), the entities (Task 6).
- Produces:

```csharp
public sealed record LlmRequestConfig(string ApiBase, string ApiKey, string Model, LlmParameters Parameters)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);
    public int TimeoutSeconds => Parameters.TimeoutSeconds;
    public string? ReasoningEffort => Parameters.ReasoningEnabled ? Parameters.ReasoningEffort : null;
    public int ContextCharBudget { get; init; } = LlmContextBudget.MinimumChars;
    public bool IncludeStreamUsage { get; init; } = true;
}

public interface ILlmSettingsResolver
{
    Task<LlmRequestConfig> ResolveAsync(LlmCallKind kind, CancellationToken ct = default);
}
```

**Two deliberate design points.** `ResolveAsync` takes the kind **explicitly** rather than reading `LlmCallScope.Active`, even though the scope already carries it: ambient is acceptable for telemetry (a missing scope logs `Unknown`, which is visible and fixable) but not for behaviour, where a missing scope would silently apply the wrong model and parameters. Every caller already knows its kind - it pushes the scope with it. And `userId` is **gone** from the signature: configuration is no longer per user.

`SummarizationRequestConfig` is renamed to `LlmRequestConfig` because it was never summarisation-specific. This is a mechanical rename across the 7 clients and every processor; do it in one pass with the compiler as the guide.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Configuration;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace Diariz.Api.Tests.Llm;

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
        // Persisting it from the seeder would resurrect a row the admin deliberately deleted - the same
        // defect as the user-role backfill that kept undoing demotions.
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
        Assert.Equal(0.0, tags.Parameters.Temperature);   // group override
        Assert.Equal(0.9, tags.Parameters.TopP);          // model base
        Assert.Equal(120, tags.Parameters.TimeoutSeconds); // app default

        var chat = await Build(db).ResolveAsync(LlmCallKind.ChatMessage);
        Assert.Equal(0.5, chat.Parameters.Temperature);   // model base, no group override
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
```

`FakeApiKeyProtector` may already exist in `Diariz.Api.TestSupport`; if not, add one there returning its input unchanged - do not reach for a mocking library.

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmSettingsResolver"
```

Expected: compile error.

- [ ] **Step 3: Write the resolver**

`src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs`. The shape, with the layer walk as its core:

```csharp
    public async Task<LlmRequestConfig> ResolveAsync(LlmCallKind kind, CancellationToken ct = default)
    {
        var group = LlmCallGroups.GroupFor(kind);
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId, ct);

        var model = await ChooseModelAsync(group, ps, ct);

        // Most specific first. A null layer is skipped, so a model with no override row for this group
        // simply inherits rather than omitting everything.
        var layers = new List<string?>
        {
            group is null ? null : ParametersFor(model, group.Value),
            model is null ? null : ParametersFor(model, LlmCallGroup.ModelBase),
            _defaults.LayerFor(group),
            _defaults.BaseLayer,
        };

        var parameters = LlmParameterLayers.Resolve(layers);

        // The environment fallback keeps the admin's tuned platform timeout, which a migration could not
        // move into a model row (the endpoint lives in configuration, not the database).
        if (model is null && ps is not null)
            parameters = parameters with { TimeoutSeconds = ps.LlmTimeoutSeconds };

        return new LlmRequestConfig(
            ApiBase: model?.ApiBase ?? _summary.ApiBase,
            ApiKey: model is null ? _summary.ApiKey : (_protector.Unprotect(model.ApiKeyEncrypted) ?? ""),
            Model: model?.Name ?? _summary.Model,
            Parameters: parameters)
        {
            ContextCharBudget = LlmContextBudget.CharsFor(model?.ContextLength ?? _chat.ContextLength),
            IncludeStreamUsage = ps?.LlmStreamUsageEnabled ?? true,
        };
    }
```

Write `ChooseModelAsync` (assignment for the group, else `ps.DefaultLlmModelId`, else null) and `ParametersFor` (the model's loaded parameter rows, or null). Load the model with `.Include(m => m.Parameters)` in a single query - **do not** add an `Include` to `RecordingsController.Get`; that is a different endpoint whose cartesian blow-up cost 17x once.

- [ ] **Step 4: Replace the old resolver at every call site**

Delete `SummarizationSettingsResolver.cs`, register `ILlmSettingsResolver` in `Program.cs` in its place, and let the compiler find every call. Each becomes `await resolver.ResolveAsync(LlmCallKind.X, ct)` with the kind the enclosing `LlmCallScope.Push` already uses. Rename `SummarizationRequestConfig` to `LlmRequestConfig` throughout.

- [ ] **Step 5: Run the resolver tests and the whole unit suite**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: PASS, including the Task 1 characterisation tests **unchanged** - that is the proof the refactor preserved behaviour.

- [ ] **Step 6: Mutation-verify the layer order**

Swap the first two entries of `layers` so the model base precedes the group override. Re-run. Expected: `Walks_all_four_layers_in_order` FAILS with `0.5 != 0.0`. Revert.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api tests/Diariz.Api.Tests
git commit -m "feat(llm): resolve model and parameters from the platform, not the user"
```

---

### Task 8: Wire the clients, embeddings and dictation

**Files:**
- Modify: the 7 chat clients (one line each)
- Modify: `src/Diariz.Api/Services/EmbeddingSettingsResolver.cs`
- Modify: `src/Diariz.Api/Services/ChatStreamClient.cs` (tool gating)
- Test: extend `tests/Diariz.Api.Tests/Llm/RequestBodyCharacterisationTests.cs`

**Interfaces:**
- Consumes: `LlmRequestBody.Apply` (Task 4), `LlmRequestConfig` (Task 7).

- [ ] **Step 1: Write the failing tests**

Add to the characterisation file:

```csharp
    [Fact]
    public async Task Sends_the_parameters_the_resolver_decided()
    {
        var (client, handler) = Build(h => new SummarizationClient(h));
        var config = Config() with
        {
            Parameters = new LlmParameters { Temperature = 0.75, MaxTokens = 900, TopK = 40 },
        };

        await client.SummarizeAsync(config, Segments(), false, "T");

        var body = handler.LastBody;
        Assert.Equal(0.75, body.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(900, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(40, body.GetProperty("top_k").GetInt32());
    }

    [Fact]
    public async Task Omits_a_parameter_the_resolver_decided_not_to_send()
    {
        var (client, handler) = Build(h => new SummarizationClient(h));
        var config = Config() with { Parameters = new LlmParameters { Temperature = null } };

        await client.SummarizeAsync(config, Segments(), false, "T");

        Assert.False(handler.LastBody.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task Chat_omits_tools_when_the_model_does_not_support_them()
    {
        var (client, handler) = Build(h => new ChatStreamClient(h));
        var config = Config() with { Parameters = new LlmParameters { ToolsSupported = false } };

        var tools = new List<object> { new { type = "function", function = new { name = "noop" } } };
        await foreach (var _ in client.StreamChunksAsync(config, [new ChatMessage("user", "hi")], tools)) { }

        Assert.False(handler.LastBody.TryGetProperty("tools", out _));
        Assert.False(handler.LastBody.TryGetProperty("tool_choice", out _));
    }
```

- [ ] **Step 2: Run and watch them fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RequestBodyCharacterisation"
```

Expected: the three new tests FAIL - the clients still send hardcoded literals and ignore `Parameters`.

- [ ] **Step 3: Change each client**

In each of `SummarizationClient`, `MeetingMinutesClient`, `ActionsClient`, `TagsClient`, `TranslationClient`, and both `ChatStreamClient` methods: delete the `["temperature"] = 0.3,` (or `0.1`) line and the `if (config.ReasoningEffort is not null) body["reasoning_effort"] = ...` line, and after the body literal add:

```csharp
        LlmRequestBody.Apply(body, config.Parameters);
```

In `ChatStreamClient.StreamChunksAsync`, gate the tools line on the flag:

```csharp
        if (config.Parameters.ToolsSupported && tools is { Count: > 0 })
        { body["tools"] = tools; body["tool_choice"] = "auto"; }
```

- [ ] **Step 4: Repoint the embedding endpoint fallback**

In `EmbeddingSettingsResolver`, replace the branch that reads `s?.SummaryApiBase` and the user's encrypted key with the resolved default model's endpoint. Its `ResolveAsync` also loses its `userId` parameter. Take `ILlmSettingsResolver` rather than `SummarizationOptions` + `IApiKeyProtector`: the fallback then inherits the whole chain (model endpoint, decrypted key, timeout) instead of re-deriving it. `Embedding` is a groupless kind, so it resolves the platform default and never a group override.

Keep the dedicated-endpoint branch's own **timeout** as well as its endpoint and key - a separate embeddings service has its own deadline, and only the shared-endpoint case should inherit the model's.

**CORRECTION - dictation needs no change.** This step originally said to point `DictationClient` at the same
resolver. That is wrong. Dictation is **speech-to-text** (`/audio/transcriptions`, model `whisper-1`), a
different service from the chat-completions endpoint, and its config is built purely from `DictationOptions`
with no user-settings input - it was already platform-level, as its own endpoint description says. Pointing
it at the LLM resolver would send the chat model's name to an STT service and break dictation. Verified: no
occurrence of `UserSettings` anywhere in the dictation path.

- [ ] **Step 5: Run the full unit suite**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: PASS. Every original characterisation test must still pass **with no edit** - if one needed changing, behaviour was not preserved and the change must be re-examined rather than the test relaxed.

- [ ] **Step 6: Mutation-verify**

Remove the `LlmRequestBody.Apply` line from `SummarizationClient`. Re-run. Expected: `Sends_the_parameters_the_resolver_decided` FAILS, and so does the original `Summarization_sends_model_temperature_and_messages_only`, since nothing then sets a temperature. Restore.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api tests/Diariz.Api.Tests
git commit -m "feat(llm): send resolved parameters from every client"
```

---

### Task 9: The platform API

**Files:**
- Create: `src/Diariz.Api/Controllers/LlmModelsController.cs`
- Create: `src/Diariz.Api/Contracts/LlmModelDtos.cs`
- Test: `tests/Diariz.Api.Tests/Controllers/LlmModelsControllerTests.cs`

**Interfaces:**
- Produces the endpoints in the spec's API table. DTOs:

```csharp
public record LlmModelDto(Guid Id, string Name, string ApiBase, bool HasApiKey, int ContextLength,
    Dictionary<string, string> Parameters);   // group name -> parameters JSON

public record LlmModelUpsert(string Name, string ApiBase, string? ApiKey, int ContextLength,
    Dictionary<string, string> Parameters);

public record LlmAssignmentsDto(Guid? DefaultModelId, Dictionary<string, Guid> Assignments);
```

- [ ] **Step 1: Write the failing tests**

The construction idiom comes from `PlatformSettingsControllerTests`: a private `Build` helper, `TestDb.Create()` per test, and `Http.Context(userId)` for the `ControllerContext`.

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class LlmModelsControllerTests
{
    private static LlmModelsController Build(DiarizDbContext db, bool platformAdmin = true) =>
        new(db, new FakeApiKeyProtector(),
            Options.Create(new SummarizationOptions { ApiBase = "http://env/v1", Model = "env-model" }),
            NullLogger<LlmModelsController>.Instance)
        { ControllerContext = Http.Context(Guid.NewGuid(), platformAdmin: platformAdmin) };

    private static LlmModel Seed(DiarizDbContext db, string name = "m", string? key = "secret")
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
        new(name, "http://llm/v1", key, 8192, parameters ?? new Dictionary<string, string>());

    [Fact]
    public async Task List_refuses_a_user_who_is_not_a_platform_administrator()
    {
        using var db = TestDb.Create();
        var result = await Build(db, platformAdmin: false).List();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Never_returns_the_api_key_only_whether_one_is_set()
    {
        // Same contract the per-user key had: write-only, so a stored key cannot leak back out.
        using var db = TestDb.Create();
        Seed(db, key: "secret");

        var result = await Build(db).List();
        var dto = Assert.IsType<List<LlmModelDto>>(result.Value).Single();

        Assert.True(dto.HasApiKey);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public async Task Update_without_a_key_leaves_the_stored_key_unchanged()
    {
        // The UI cannot send back a key it was never given, so omitting it must mean "keep it".
        using var db = TestDb.Create();
        var model = Seed(db, key: "secret");

        await Build(db).Update(model.Id, Upsert(key: null));

        Assert.Equal("secret", (await db.LlmModels.AsNoTracking().SingleAsync()).ApiKeyEncrypted);
    }

    [Fact]
    public async Task Update_with_a_key_replaces_it()
    {
        using var db = TestDb.Create();
        var model = Seed(db, key: "old");

        await Build(db).Update(model.Id, Upsert(key: "new"));

        Assert.Equal("new", (await db.LlmModels.AsNoTracking().SingleAsync()).ApiKeyEncrypted);
    }

    [Fact]
    public async Task Rejects_an_unknown_parameter_name()
    {
        // A typo in the admin UI must fail loudly rather than silently doing nothing: an unrecognised key
        // would be ignored by the layer merge, so the admin would see a saved setting with no effect.
        using var db = TestDb.Create();
        var model = Seed(db);

        var result = await Build(db).Update(model.Id, Upsert(
            parameters: new Dictionary<string, string> { ["ModelBase"] = """{"temperatur":0.3}""" }));

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("temperatur", bad.Value!.ToString());
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
    public async Task Delete_of_an_assigned_model_is_refused_and_names_the_groups()
    {
        using var db = TestDb.Create();
        var model = Seed(db);
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = model.Id });
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Tags, LlmModelId = model.Id });
        await db.SaveChangesAsync();

        var result = await Build(db).Delete(model.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var message = conflict.Value!.ToString()!;
        Assert.Contains("Chat", message);
        Assert.Contains("Tags", message);
        Assert.Single(db.LlmModels);
    }

    [Fact]
    public async Task Create_from_environment_is_refused_once_any_model_exists()
    {
        using var db = TestDb.Create();
        Seed(db);

        var result = await Build(db).CreateFromEnvironment();

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Single(db.LlmModels);
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
}
```

**CORRECTION - authorisation is not unit-testable here, and `Http.Context` needs no new flag.** The
`List_refuses_a_user_who_is_not_a_platform_administrator` test above was written expecting a `ForbidResult`
from a directly-constructed controller. That cannot work: the unit harness news up the controller itself, so
an `[Authorize(Policy=...)]` attribute never executes and such an assertion would pass whether or not the
attribute existed - a test that cannot fail. Adding a `platformAdmin` flag to `Http.Context` would only make
the illusion more convincing.

The house pattern is a **separate integration class over the real pipeline**: see `PlatformWebhooksAuthTests`
and `WorkflowSignalsAuthTests`, both of which say so in their class comments. So:

- `LlmModelsControllerTests` (unit) covers CRUD, validation, the key contract and the delete guard - no
  authorisation assertions at all, and its class comment says why and points at the auth class.
- `LlmModelsAuthTests` (integration, `DiarizWebAppFactory`) proves the `ManagePlatform` gate on a read, on a
  create, and on the assignments write, plus one positive case for a Platform Administrator. Authority comes
  from group membership (`Perms.Grant(db, id, Perms.PlatformAdministrator)`), not a role claim.

- [ ] **Step 2: Run and watch them fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelsController"
```

Expected: compile error.

- [ ] **Step 3: Write the controller**

Follow `PlatformSettingsController` for the authorisation attribute and constructor shape. Validate parameter names against `LlmParameterLayers.ParameterNames` and return 400 naming the offending key. Return 409 with the group names on a delete that violates the FK.

**The controller MUST check before deleting; it cannot rely on the database refusing.** Established in Task 7
against real Postgres (`LlmModelSchemaTests`, and its class comment records the detail):

- `LlmCallAssignment.LlmModelId` is required, so EF throws `InvalidOperationException` at `DbSet.Remove`
  itself - before `SaveChangesAsync` - when the assignment is tracked. Catching `DbUpdateException` around
  the save will not see it.
- `PlatformSettings.DefaultLlmModelId` is **nullable**, so when that row is tracked EF issues
  `UPDATE PlatformSettings SET DefaultLlmModelId = NULL` ahead of the DELETE and the RESTRICT never fires -
  **the model is deleted and the platform default is silently cleared**. Verified directly: no exception,
  model gone, default nulled.

So `Delete` must query for assignments AND for `DefaultLlmModelId == id` and return 409 itself. Both FKs are
`DELETE RESTRICT` in Postgres, but that only bites on the untracked path, which is a backstop for code that
forgets - not the guard.

- [ ] **Step 4: Run and watch them pass, then build the solution**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmModelsController"
dotnet build Diariz.slnx
```

The build matters: a controller constructor change has a second construction site in `RbacIntegrationTests.cs`, and a unit-only run will not catch it.

- [ ] **Step 5: Mutation-verify the two authorisation-shaped assertions**

Three mutations, all verified:

1. Remove `[Authorize(Policy = "ManagePlatform")]` -> the three negative cases in `LlmModelsAuthTests` FAIL
   (the positive one still passes, as it should).
2. Leak the stored key into a returned field -> `Never_returns_the_api_key_only_whether_one_is_set` FAILS.
3. Remove the `DefaultLlmModelId` check from `Delete` -> `Delete_of_the_platform_default_is_refused` FAILS.
   This is the one that matters most: Postgres does not stop that delete, so without the check nothing does.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api tests/Diariz.Api.Tests
git commit -m "feat(llm): platform-admin API for models, parameters and assignments"
```

---

### Task 10: The admin page

**Files:**
- Create: `apps/web/src/pages/LlmModels.tsx` and `components/llmmodels/{ModelList,ModelEditorModal,ParameterPanel,ParameterField}.tsx`, `parameterSchema.ts`
- Modify: `apps/web/src/App.tsx`, `lib/api.ts`, `components/SettingsModal.tsx`, the four `locales/*/admin.json`
- Test: `components/llmmodels/*.test.tsx`, `pages/LlmModels.test.tsx`

**Interfaces:**
- Consumes: the Task 9 endpoints.
- `parameterSchema.ts` exports `PARAMETERS: { key: string; label: string; kind: "number" | "integer" | "text" | "boolean"; min?: number; max?: number }[]` - 13 entries, keys matching `LlmParameterLayers.ParameterNames` exactly.

**Background you need:** follow `/admin/llm-usage` exactly - lazy route inside `RequireAuth` in `App.tsx`, and the Platform Administrator gate **inside the page** rendering a refusal (`LlmUsage.tsx:154`), not on the route. Web tests use plain assertions; **jest-dom is not installed** and must not be added. Mock `../lib/api` with `vi.mock`, render inside `MemoryRouter` + `QueryClientProvider`.

- [ ] **Step 1: Write the failing tests**

`ParameterField.test.tsx` - the tri-state control, the piece most likely to be got wrong:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ParameterField from "./ParameterField";

describe("ParameterField", () => {
  const base = { name: "temperature", label: "Temperature", kind: "number" as const, inherited: 0.3 };

  it("shows the inherited value when nothing is set here", () => {
    render(<ParameterField {...base} value={undefined} onChange={vi.fn()} />);
    // The admin must be able to see what they are inheriting before deciding to override it.
    expect(screen.getByText(/0\.3/)).toBeTruthy();
  });

  it("emits null when switched to Off", () => {
    const onChange = vi.fn();
    render(<ParameterField {...base} value={undefined} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: /off/i }));
    expect(onChange).toHaveBeenCalledWith(null);
  });

  it("emits the number when given a value", () => {
    const onChange = vi.fn();
    render(<ParameterField {...base} value={0.5} onChange={onChange} />);
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "0.7" } });
    expect(onChange).toHaveBeenCalledWith(0.7);
  });

  it("switches back to Inherit by removing the key, not by sending null", () => {
    // The important one. null means "send nothing" to the resolver, so emitting null here would silently
    // turn an inherited 0.3 into an omitted parameter - a behaviour change the admin never asked for.
    const onChange = vi.fn();
    render(<ParameterField {...base} value={0.5} onChange={onChange} />);
    fireEvent.click(screen.getByRole("button", { name: /inherit/i }));
    expect(onChange).toHaveBeenCalledWith(undefined);
  });
});
```

`ModelEditorModal.test.tsx`:

```tsx
import { render, screen, fireEvent, within } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";

const api = { listModels: vi.fn(), updateModel: vi.fn(), createModel: vi.fn() };
vi.mock("../../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import ModelEditorModal from "./ModelEditorModal";

const MODELS = [
  { id: "a", name: "gpt-oss-20b", apiBase: "http://a/v1", hasApiKey: true, contextLength: 8192,
    parameters: { ModelBase: '{"temperature":0.5}' } },
  { id: "b", name: "qwen3-27b", apiBase: "http://b/v1", hasApiKey: false, contextLength: 32768,
    parameters: { ModelBase: '{"temperature":0.9}', Translation: '{"temperature":0.1}' } },
];

describe("ModelEditorModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("renders a Defaults panel plus one panel per call group", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} />);
    for (const label of ["Defaults", "Tags", "Actions", "Summaries", "Minutes and formulas", "Translation", "Chat"])
      expect(screen.getByText(label)).toBeTruthy();
  });

  it("copies another model's parameters into the editor without saving", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} />);
    fireEvent.change(screen.getByLabelText(/copy from/i), { target: { value: "b" } });

    // Loaded into the open editor for review...
    expect((screen.getByTestId("param-ModelBase-temperature") as HTMLInputElement).value).toBe("0.9");
    // ...but nothing is persisted until the admin saves.
    expect(api.updateModel).not.toHaveBeenCalled();
  });

  it("never copies name, endpoint or key", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} />);
    fireEvent.change(screen.getByLabelText(/copy from/i), { target: { value: "b" } });

    // These are what make an entry distinct; copying them would produce a duplicate pointing elsewhere.
    expect((screen.getByLabelText(/name/i) as HTMLInputElement).value).toBe("gpt-oss-20b");
    expect((screen.getByLabelText(/endpoint/i) as HTMLInputElement).value).toBe("http://a/v1");
  });
});
```

`LlmModels.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi } from "vitest";

const authState = { isPlatformAdmin: true };
vi.mock("../auth", () => ({ useAuth: () => authState }));
const api = { listModels: vi.fn().mockResolvedValue([]), createModelFromEnvironment: vi.fn() };
vi.mock("../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import LlmModels from "./LlmModels";

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter><LlmModels /></MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("LlmModels", () => {
  it("refuses to render the models for anyone who is not a platform administrator", () => {
    authState.isPlatformAdmin = false;
    renderPage();
    // The route only proves someone is signed in; the gate lives here, as it does in LlmUsage.
    expect(screen.queryByRole("button", { name: /add model/i })).toBeNull();
    authState.isPlatformAdmin = true;
  });

  it("offers Create from environment when no models exist", async () => {
    renderPage();
    expect(await screen.findByRole("button", { name: /create from environment/i })).toBeTruthy();
  });
});
```

The fourth `ParameterField` test is the one that matters most: switching back to Inherit must **remove the key**, because leaving `null` would mean "omit" and silently change behaviour.

- [ ] **Step 2: Run and watch them fail**

```bash
cd apps/web && npx vitest run src/components/llmmodels src/pages/LlmModels.test.tsx
```

Expected: FAIL - the modules do not exist.

- [ ] **Step 3: Build the components**

Start with `parameterSchema.ts` (data), then `ParameterField` (one control), then `ParameterPanel` (a map over the schema), then `ModelEditorModal`, then `ModelList`, then the page. Add the route to `App.tsx` beside `/admin/llm-usage`, and a link from the Settings AI tab beside the existing `href="/admin/llm-usage"`.

- [ ] **Step 4: Run the full web suite**

```bash
cd apps/web && npm test && npm run build
```

Expected: PASS with no new warnings, and a clean `tsc`.

- [ ] **Step 5: Mutation-verify the tri-state**

Make "Inherit" emit `null` instead of removing the key. Expected: the fourth `ParameterField` test FAILS. Revert.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src
git commit -m "feat(llm): admin page for models, parameters and assignments"
```

---

### Task 11: Remove the per-user model settings

> **MOVED HERE FROM TASK 6:** dropping the seven `UserSettings` columns belongs in this task, because this
> is where their consumers go. Doing it earlier leaves the branch unbuildable.

**Files:**
- Modify: `src/Diariz.Domain/Entities/UserSettings.cs` - remove `SummaryApiBase`, `SummaryApiKeyEncrypted`, `SummaryModel`, `ChatContextWindow`, `LlmTimeoutSeconds`, `ReasoningEnabled`, `ReasoningEffort`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs` - drop the two `HasMaxLength` lines for `SummaryApiBase` / `SummaryModel` in the `UserSettings` block
- Create: a second migration dropping those seven columns (no `CurrentFormat` bump - restore runs `pg_restore --clean` then migrates up)
- Modify: `apps/web/src/components/assistant/AssistantSection.tsx` (drop the Model card, keep Tools)
- Delete: `apps/web/src/components/assistant/ModelDialog.tsx` and `ModelDialog.test.tsx`
- Modify: `ChatContextResolver.cs`, the user-settings controller and DTO, `apps/web/src/lib/api.ts`, the four locale catalogues
- Test: update `AssistantSection.test.tsx`; move the dropped-columns `information_schema` assertion here from Task 6

- [ ] **Step 1: Write the failing test**

In `AssistantSection.test.tsx`:

```tsx
it("no longer offers a model override", () => {
  render(<AssistantSection />);
  expect(screen.queryByText(/change model/i)).toBeNull();
});

it("still offers the tool selection", () => {
  render(<AssistantSection />);
  expect(screen.getByText(/tools/i)).toBeTruthy();
});
```

- [ ] **Step 2: Run and watch the first fail**

```bash
cd apps/web && npx vitest run src/components/assistant
```

Expected: the first FAILS (the control is still there), the second passes.

- [ ] **Step 3: Remove the card and the dialog**

Delete the Model card from `AssistantSection.tsx`, delete both `ModelDialog` files, and remove the seven fields from the user-settings DTO, controller, and `api.ts`. Remove the now-unused i18n keys from all four catalogues.

- [ ] **Step 4: Run everything**

```bash
cd apps/web && npm test && npm run build
dotnet test tests/Diariz.Api.Tests
dotnet build Diariz.slnx
```

Expected: PASS. Fix any test that referenced a removed field.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src src/Diariz.Api tests
git commit -m "feat(llm): remove per-user model configuration"
```

---

### Task 12: Release, docs and the PR

- [ ] **Step 1: Bump the version in all five places**

`version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `integrations/n8n-nodes-diariz/package.json` (all `"version": "0.221.0"`), and `src/Diariz.Api/Diariz.Api.csproj` (`<Version>0.221.0</Version>`).

- [ ] **Step 2: Run the version tests**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: mirrors PASS, releases FAILS until the next step.

- [ ] **Step 3: Add the release entry**

`RELEASES[0]` in `apps/web/src/lib/releases.ts`, `pr: 0` for now. The summary must state plainly that per-user model configuration is gone, what replaces it, and that existing deployments keep working unchanged until an administrator configures a model. Plain hyphens only.

- [ ] **Step 4: Update the docs**

| Doc | What |
|---|---|
| `docs/Data_Schema.md` | Three new tables with every column, key, index and cascade; the seven dropped `UserSettings` columns; `PlatformSettings.DefaultLlmModelId`; `LlmCallGroup`; the `jsonb` columns; the migration-history row |
| `docs/Overall_Synopsis_of_Platform.md` | The four-layer resolution replacing the per-user chain; the environment fallback and why it is not seeded; the new admin page and endpoints |
| README Features row + `docs/features.md` bullet + `CAPABILITIES` row | In lockstep, all three |
| `apps/web/src/content/help/en/ai-model-settings.md` | **Rewrite.** It documents the per-user settings this removes, so leaving it would actively mislead. ASCII only; keep the front-matter block |

- [ ] **Step 5: Check for stray dashes in what you added**

```bash
python -c "import io, subprocess; raw = subprocess.run(['git','diff','main...HEAD','--unified=0'], capture_output=True).stdout; t = raw.decode('utf-8','replace'); added=[l for l in t.splitlines() if l.startswith('+') and not l.startswith('+++')]; bad=[l for l in added if any(c in l for c in '\u2014\u2013')]; print('added:',len(added),'| with dash:',len(bad)); [print('  ',l[:120]) for l in bad]"
```

Expected: `with dash: 0`. Scope it to the diff - `README.md` and `docs/features.md` already carry many pre-existing dashes - and do not pipe `git diff` into python, which decodes as cp1252 on this machine and reports a false zero.

- [ ] **Step 6: Full verification**

```bash
dotnet build Diariz.slnx
dotnet test
cd apps/web && npm test && npm run build
```

Expected: everything green. Docker must be running for the integration tests.

- [ ] **Step 7: Live check**

```bash
cd deploy && docker compose up --build -d
```

With an empty `LlmModel` table, run a summary and confirm from the LLM usage log that it still completes and that the model and duration are as before - the behaviour-preservation claim, checked in the running app rather than only in tests. Then add a model on `/admin/llm-models`, assign Tags to it, and confirm a tag extraction uses it.

- [ ] **Step 8: Push and open the PR**

```bash
git push -u origin feat/platform-llm-models
```

The body must state: what changed and why; that per-user model settings are removed and existing deployments keep working until a model is configured; **deployment surface: server redeploy only** (nothing in `apps/desktop/**` changes); the new env variables; and the live-check results.

- [ ] **Step 9: Correct the PR number**

Read the real number, replace `pr: 0`, commit and push. Do not guess "last + 1" - Dependabot and issues share the sequence, and no test catches a wrong number.
