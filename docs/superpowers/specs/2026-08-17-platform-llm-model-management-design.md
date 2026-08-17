# Platform-level LLM model and parameter management

Date: 2026-08-17
Status: approved, not yet implemented

## Problem

Every LLM setting that matters is either **hardcoded** or **owned by individual users**, and neither is
acceptable now that the platform is heading into production.

**Hardcoded.** The sampling parameters are literals at each call site. `temperature` is `0.3` in
`SummarizationClient`, `MeetingMinutesClient`, `ActionsClient`, `TagsClient`, and both `ChatStreamClient`
methods, and `0.1` in `TranslationClient`. No `max_tokens`, `top_p`, `top_k`, `repeat_penalty`,
`frequency_penalty`, or `presence_penalty` is ever sent - output length is entirely at the server's
discretion. None of it is reachable from configuration at any level, and **no test asserts any of it**: a
grep of the test sources finds no assertion on a request body, so any of those literals could change and
CI would stay green.

**User-owned.** Endpoint, API key, model, timeout, reasoning on/off and reasoning effort are all resolved
per recording-owner from `UserSettings`, falling back to server options. That made sense when Diariz was a
personal tool. It means each user can point the platform at an arbitrary endpoint with an arbitrary model,
and an administrator has no way to see or control what is actually being called.

There is also no way to express that different models want different settings. A 20B general model and a
27B reasoning model do not share a good temperature, and their reasoning-effort vocabularies differ -
`gpt-oss-20b` takes `low|medium|high` while `qwen3` accepts `xhigh`. Today there is one global set of
values and one global effort string.

## Goal

Move all model and parameter management to the Platform Administrator, stored in the database, organised
as **per-model parameter sets with per-call-type overrides**, with meaningful app defaults that are
overridable from the environment. Close the test gap by pinning every client's request body.

## Non-goals

- **Embeddings and dictation stay on environment configuration.** Their bodies are `{model, input}` and a
  multipart audio post; they take no sampling parameters, so they would occupy the parameter UI with fields
  that do nothing. The one necessary change is that their endpoint fallback stops borrowing the user's
  summarisation endpoint (which is being removed) and uses the resolved default model's endpoint instead.
- No per-user model configuration of any kind survives. This is the point of the change, not a casualty.
- No prompt management. Prompts remain in code and in Formula templates.
- No cost or rate-limit modelling.
- No change to which endpoint protocol is spoken. Everything remains OpenAI-compatible.

## Decisions taken during design

Recorded because each closes off an approach a reader might otherwise expect:

| Decision | Rejected alternative and why |
|---|---|
| One PR | Phasing into three was offered and declined. The diff is large; §Review burden below states what that costs. |
| Platform default model + optional per-call-type override | "One active model for everything" cannot put tag extraction on a small model; "per call type always" makes the admin choose six times for no gain. |
| Endpoint and key **per model** | A single platform endpoint cannot mix a local LM Studio model with a cloud one. Making the model entry self-contained means pointing a call type at a model brings its connection with it. |
| Model base + per-call-type override | Per-call-type only would mean setting a model's temperature six times. Model base only would drop the per-call-type requirement and lose today's deliberate 0.1 for translation. |
| jsonb, absent = inherit / null = omit / value = send | A `-1` sentinel is unsafe: `-1` is a legal, meaningful value for `max_tokens` (unlimited) and `top_k` (disabled) on some OpenAI-compatible servers, so it would make those two impossible to set honestly. Wide nullable columns would need a migration per future parameter and split one parameter across two places. |
| A dedicated `/admin/llm-models` page | Putting this in the Settings modal would roughly double `SettingsModal.tsx`, already 803 lines with the AI tab inline. |

## Domain model

Three new tables.

### `LlmModel`

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `Name` | `text` | **Unique.** The literal string sent as `model`, e.g. `openai/gpt-oss-20b` |
| `ApiBase` | `text` | OpenAI-compatible base URL |
| `ApiKeyEncrypted` | `text?` | Encrypted at rest via the existing `IApiKeyProtector`. Write-only over the API |
| `ContextLength` | `int` | The model's context window. Moved off `UserSettings.ChatContextWindow` and `Chat:ContextLength` - a context window is a fact about the model, not a preference |
| `CreatedAt` / `UpdatedAt` | `timestamptz` | Stored via `.ToUniversalTime()`; Npgsql rejects a non-zero offset for `timestamptz` |

### `LlmModelParameters`

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `LlmModelId` | `Guid` | FK to `LlmModel`, **cascade delete** |
| `Group` | `int` | `LlmCallGroup`. **Append only, never renumber** - same rule as `RecordingSource` |
| `ParametersJson` | `jsonb` | The parameter set for this scope |

Unique on `(LlmModelId, Group)`.

`Group` is **not nullable**, and `LlmCallGroup.ModelBase = 0` is a real enum member rather than a null
marker. Postgres treats NULLs as distinct in a unique index, so a nullable "this is the base" column would
silently permit two base rows per model - the same class of bug the one-Personal-room-per-user filtered
index exists to prevent.

```csharp
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

### `LlmCallAssignment`

| Column | Type | Notes |
|---|---|---|
| `Group` | `int` | PK. Never `ModelBase` - validated on write |
| `LlmModelId` | `Guid` | FK to `LlmModel`, **`ON DELETE RESTRICT`** |

At most six rows. `RESTRICT` rather than `SET NULL` so deleting a model that is in use fails loudly and the
UI can say *"used by Tags and Chat"*, instead of silently re-routing those call types to the default model.

### `PlatformSettings` gains one column

| Column | Type | Notes |
|---|---|---|
| `DefaultLlmModelId` | `Guid?` | FK to `LlmModel`, **`ON DELETE RESTRICT`**. The model used by any call group with no explicit assignment. Null means fall through to the environment fallback |

`RESTRICT` for the same reason as `LlmCallAssignment`: deleting the model every unassigned group depends on
should fail with an explanation, not quietly repoint the whole platform at the environment fallback.

### Mapping `LlmCallKind` to `LlmCallGroup`

`LlmCallKind` has 15 members and is **not** replaced - it stays exactly as it is for the usage log. A pure
function maps it to the group whose parameters apply:

| Group | Kinds |
|---|---|
| `Tags` | `Tags` |
| `Actions` | `ExtractActions` |
| `Summaries` | `Summarize`, `SectionSummary` |
| `MinutesAndFormulas` | `MeetingMinutes`, `SectionMinutes`, `MeetingTypeMinutes`, `FormulaRun` |
| `Translation` | `Translation` |
| `Chat` | `ChatMessage`, `ChatTitle` |
| *(no group)* | `Dictation`, `Embedding`, `SearchQuery`, `Unknown` - different endpoints, no sampling parameters |

The mapping is a pure function over the enum with no default arm, so **adding a member to `LlmCallKind`
without deciding its group is a compile error**, not a silent fallthrough to `Chat`.

## The parameter set

Thirteen parameters, in two kinds. The distinction matters because they are consumed in different places.

**Wire parameters** - serialised into the request body:

| Parameter | JSON type |
|---|---|
| `temperature` | number |
| `top_p` | number |
| `top_k` | integer |
| `repeat_penalty` | number |
| `frequency_penalty` | number |
| `presence_penalty` | number |
| `max_tokens` | integer |
| `max_completion_tokens` | integer |
| `reasoning_effort` | **string, free text** - `low`/`medium`/`high` for `gpt-oss`, `xhigh` for qwen3, and whatever a future model wants |

**Behaviour flags** - never serialised, they govern the client:

| Flag | Effect |
|---|---|
| `reasoning_enabled` | When false, `reasoning_effort` is omitted entirely, so a non-reasoning endpoint never sees the field |
| `timeout_seconds` | The `CancellationTokenSource` deadline. The HTTP clients have no cap of their own, so this is the only authority |
| `tools_supported` | When false, `tools` and `tool_choice` are omitted even if the caller passed tools |
| `images_supported` | Declared and stored, not yet read by any call site. Present so the schema does not need revisiting later |

`repeat_penalty` is not an OpenAI-standard field - OpenAI proper uses the two penalties - but LM Studio and
llama.cpp accept it. It is passed through verbatim; sending it to an endpoint that rejects unknown fields is
what "omit" is for.

### Encoding in `ParametersJson`

```jsonc
{
  "temperature": 0.3,        // send temperature: 0.3
  "top_k": null,             // send NO top_k, even if a lower layer sets one
  // "top_p" absent          // inherit from the next layer down
}
```

Three states per parameter, and the distinction between the last two is load-bearing: with layered
defaults, *"I have not set this here"* and *"do not send this at all"* are different instructions. Postgres
`jsonb` and `System.Text.Json` both distinguish an absent key from a present null cleanly.

Unknown keys are ignored on read and rejected on write, so a typo in the admin UI cannot silently do
nothing.

## Resolution

For a call of kind *K*, in group *G* = `GroupFor(K)`:

**1. Choose the model**

```
LlmCallAssignment[G]  ??  PlatformSettings.DefaultLlmModelId  ??  the environment fallback model
```

**2. Resolve each parameter** - walk the layers, first hit wins:

```
modelParams[G]  ->  modelParams[ModelBase]  ->  appDefaults[G]  ->  appDefaults[base]
```

At each layer: key absent means continue; key present with a value means send it and stop; key present with
`null` means **omit the parameter entirely** and stop. A parameter no layer mentions is not sent.

For a kind with no group (`Embedding`, `SearchQuery`, `Dictation`), only the model, endpoint, key and
timeout are resolved - there are no sampling parameters to walk.

### App defaults are group-capable

This is what makes the change behaviour-preserving. Defaults live in a new `LlmDefaults` options section,
bound from configuration and therefore overridable per field by environment variable:

```
LlmDefaults:Temperature = 0.3
LlmDefaults:TimeoutSeconds = 120
LlmDefaults:ReasoningEnabled = false
LlmDefaults:ToolsSupported = true
LlmDefaults:Translation:Temperature = 0.1
```

```
LlmDefaults__Temperature=0.3
LlmDefaults__Translation__Temperature=0.1
```

The shipped defaults reproduce today's request bodies exactly: `temperature` 0.3 everywhere, 0.1 for
translation, no token caps, no other sampling parameters, reasoning off.

**With an empty database and no environment overrides, every request body is byte-identical to today's.**
That is an explicit design requirement and is asserted by tests, not merely intended.

### The environment fallback model

An existing deployment upgrades with `SUMMARY_API_BASE` set and zero `LlmModel` rows. When the table is
empty the resolver **synthesizes** a model from `Summarization:ApiBase` / `ApiKey` / `Model`, plus
`PlatformSettings.LlmTimeoutSeconds` for its timeout and `Chat:ContextLength` for its window.

It is deliberately **not persisted**. Writing it from the seeder would be the same defect as the user-role
backfill that kept resurrecting demoted accounts: the seeder runs on every boot, so a row the administrator
deliberately deleted would silently come back. The admin page shows *"No models configured - using
SUMMARY_API_BASE"* with a **Create from environment** button that persists it once, on an explicit click.

## What is removed

`UserSettings` loses seven columns: `SummaryApiBase`, `SummaryApiKeyEncrypted`, `SummaryModel`,
`ChatContextWindow`, `LlmTimeoutSeconds`, `ReasoningEnabled`, `ReasoningEffort`.

Kept, deliberately: `ChatToolsEnabled` and `ChatToolOverridesJson` (which tools a user wants is a
preference, distinct from the platform declaring a model *capable* of tool calling - the platform flag now
gates the user's choice), `NativeLanguage`, and everything unrelated to LLM configuration.

Web: the Assistant tab keeps its Tools card and loses its Model card; `ModelDialog.tsx` is deleted. The
`api/user/settings` contract loses the seven fields.

`PlatformSettings.LlmTimeoutSeconds` is **kept and marked obsolete**. A migration cannot fold it into a
model row because the endpoint lives in the environment, not the database, so dropping it would silently
reset a tuned production timeout to the app default. Instead the synthesized fallback model reads it, which
means upgrading changes nothing; it becomes unreachable once real models exist and can be dropped in a
later release.

### Backup compatibility

**No `CurrentFormat` bump.** Restore runs `pg_restore --clean` - which restores the backup's own schema,
columns and all - and then calls `MigrateToCurrentAsync` to roll it up to the running code. An older
backup's `UserSettings` LLM columns therefore restore successfully and are then dropped by this migration.
Existing backups keep working.

## Admin UI

A dedicated page at `/admin/llm-models`, following `/admin/llm-usage` in every respect:

| Aspect | Approach |
|---|---|
| Route | `App.tsx`, lazy-loaded inside `RequireAuth` + `Suspense` |
| Authorisation | **Inside the page.** `RequireAuth` only proves someone is signed in; the page renders a refusal for anyone who is not a Platform Administrator, as `LlmUsage.tsx` does |
| Page | `pages/LlmModels.tsx` |
| Sub-components | `components/llmmodels/` |
| Entry point | A link on the Settings AI tab beside the existing `/admin/llm-usage` link |

`SettingsModal.tsx` gains one link and nothing else.

| Component | Responsibility |
|---|---|
| `ModelList.tsx` | The list: name, endpoint host, context length, and which call groups each model serves. Add / edit / delete. Delete of an in-use model is refused with the groups named |
| `ModelEditorModal.tsx` | The popout: identity fields, then a **Defaults** panel plus six group panels that stay visually empty until touched. Hosts the copy control |
| `ParameterPanel.tsx` | One scope's fields, generated from the schema rather than hand-written seven times |
| `ParameterField.tsx` | One tri-state control: **Inherit** / **Off** / a value, showing the inherited value greyed out when set to Inherit, so the admin can see what they are inheriting before overriding it |
| `parameterSchema.ts` | The 13 parameters, their types and ranges, in one place. The single source both the panels and validation read |

**Copy.** A *Copy from...* picker in the editor loads another model's parameter sets into the open editor
without saving, so the admin reviews before committing. It copies parameters only - never name, endpoint,
or key, which are what make a model entry distinct.

## API

Platform Administrator only, enforced server-side rather than by the page's own guard:

| Endpoint | Purpose |
|---|---|
| `GET /api/platform/llm-models` | List. Returns `hasApiKey`, never the key |
| `POST /api/platform/llm-models` | Create |
| `PUT /api/platform/llm-models/{id}` | Update, including parameter sets. Omitting the key leaves it unchanged |
| `DELETE /api/platform/llm-models/{id}` | Refused with 409 and the group names when the model is assigned |
| `GET` / `PUT /api/platform/llm-assignments` | The call-group to model mapping, plus the default model |
| `POST /api/platform/llm-models/from-environment` | Persists the synthesized fallback. 409 if any model already exists |

The API key is write-only in exactly the pattern `api/user/settings` already uses for the per-user key that
is being removed.

## Testing

TDD throughout. The request-body tests come **first**: they are the characterisation tests that prove the
rest of the change does not alter behaviour.

| Layer | What it pins |
|---|---|
| **Request body** | A capturing `HttpMessageHandler` asserting the exact JSON each of the 7 chat clients sends. Written and passing **before** any refactor, against today's hardcoded literals, so it fails if the new resolution changes any body it should not |
| Omission | An omitted parameter is **absent from the JSON**, not serialised as `null`. Sending `"top_k": null` to a server that validates types is a 400, so this is a real distinction, not a stylistic one |
| Resolution | The four-layer walk; absent vs null vs value at each layer; group-capable app defaults; the no-group kinds |
| Enum mapping | Every `LlmCallKind` maps to a group or is explicitly groupless - a test enumerating the enum so a new kind cannot be forgotten |
| Environment fallback | Synthesized when the table is empty; **not** persisted; superseded once a row exists |
| jsonb | Real-Postgres round-trip of all three states. The in-memory provider does not model `jsonb`, and byte-comparing a `jsonb` column's text never matches because Postgres reformats it - compare parsed values |
| Unique indexes | Two `ModelBase` rows for one model are rejected; two models with one name are rejected. Postgres-only, so integration |
| Delete restrict | Deleting an assigned model fails; the API surfaces 409 with group names |
| API | Platform-Administrator-only on every endpoint; key write-only; key preserved when omitted on update |
| Migration | Integration: the seven columns are gone, unrelated `UserSettings` data survives, existing rows are intact |
| Web | The list; the editor's seven panels; copy loads without saving; the tri-state field in all three states; the non-admin refusal |

Every assertion is mutation-verified - break the thing, watch that specific test fail with a real message.
Assertions that check for absence (an omitted parameter, a refused delete) are the ones most likely to pass
vacuously, so they get explicit attention.

## Review burden

One PR was chosen over three. This spec touches roughly 50 files across the domain, API, and web, including
a destructive migration and the deletion of a user-facing settings surface. Two consequences worth stating
rather than discovering at review time: there is no intermediate commit at which parameters are
configurable but not yet editable, so the whole path is exercised only at the end; and the
behaviour-preservation claim rests entirely on the request-body characterisation tests, which is why they
are written first and why every one of them is mutation-verified.

## Release

Functional enhancement: **0.220.0 -> 0.221.0**, across `version.json` and its four mirrors.

Documentation is substantial and not optional:

- `docs/Data_Schema.md` - three new tables, seven dropped columns, the new enum, the `jsonb` columns
- `docs/Overall_Synopsis_of_Platform.md` - the four-layer resolution replaces the per-user chain described
  today; the new admin page and endpoints; the environment fallback
- README Features row, `docs/features.md` bullet, About-box `CAPABILITIES` row - all three in lockstep
- **`apps/web/src/content/help/en/ai-model-settings.md` must be rewritten.** It currently documents the
  per-user model settings this removes, so leaving it would actively mislead. This is a behaviour change a
  user relies on, which is exactly when a help article must be updated
- Release notes entry stating plainly that per-user model configuration is gone and what replaces it

**Deployment surface: server redeploy only.** Nothing in `apps/desktop/**` changes.
