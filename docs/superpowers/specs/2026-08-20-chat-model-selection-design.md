# Chat model selection - design

Date: 2026-08-20
Status: approved
Ships as: two PRs - `0.231.0` (labels + in-chat set + picker) and `0.232.0` (endpoint discovery)

## Problem

Chat runs on exactly one model, chosen by a Platform Administrator in the `Chat` column of the routing
grid at `/admin/llm-models`. A user who wants a bigger context window, a faster model, or a second
opinion has no way to get one, and an administrator who wants to offer a choice has no way to express it -
the grid's contract is one dot per column.

Three secondary problems fall out of the same place:

- Models are identified by their raw slug (`qwen3.8-27b@q4_k_xl`). That is the string the endpoint needs,
  but it is not a name to put in front of a user choosing between models.
- Adding the models on one LM Studio server means typing each one in by hand, even though the server can
  list them.
- The chat context dial reports the window of whichever model the platform assigned. Once the user can
  choose, it has to follow the choice, or the gauge and the actual truncation stop agreeing.

## Decisions taken

Recorded because each closed off an alternative that will look reasonable later.

1. **The `Chat` column keeps its single dot.** Multi-select becomes a *separate* `In chat` checkbox column.
   Resolution semantics for `LlmCallGroup.Chat` are therefore unchanged: the dot is still the model that
   serves a new conversation, an auto-generated title, and any API or MCP caller that sends no model.
   Turning the `Chat` column itself into checkboxes was rejected because something still has to be the
   default, and inferring it from checkbox order would make the default move when a model is renamed.
2. **The chat default is always offered, implicitly.** Its checkbox renders ticked and disabled. The
   picker can therefore never be empty, and can never exclude the model actually in use.
3. **The picker is always shown**, even with one model to choose from - so the affordance is
   discoverable and its position in the toolbar never moves.
4. **`ChatTitle` never follows the picked model.** Titling is background housekeeping the user never sees;
   pinning it to the chat default keeps a slow or expensive picked model out of it. The picker means
   "which model answers me", nothing more.
5. **The user's pick is remembered per user and stored on each saved conversation.** Reopening a saved
   conversation restores the model it was using, falling back to the default when that model is no longer
   offered.
6. **Discovery accepts a free-text base URL.** This is a deliberate relaxation of the rule the test
   endpoint established (see "SSRF" below).
7. **Imported models default to `16384` context length** when the endpoint reports none. Not the editor's
   `8192` default: 16k is the minimum useful window for transcript chat, and an import that silently
   under-sizes a model truncates real context.

## PR 1 - labels, the in-chat set, and the picker (`0.231.0`)

### Schema (one migration, all additive)

| Change | Detail |
|---|---|
| `LlmModels.DisplayName` | `text NULL`. Null or blank falls back to `Name`. Entity exposes `Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName`. |
| `LlmModels.ChatEnabled` | `boolean NOT NULL DEFAULT false` - offered in the chat picker. |
| `UserSettings.ChatModelId` | `uuid NULL`, FK to `LlmModels`, `ON DELETE SET NULL` declared explicitly. |
| `ChatSession.ContextJson` | Gains a `modelId` key. No DDL - it is already a JSON blob and older rows read the missing key as null. |

**No backfill.** Because the chat default is offered implicitly (decision 2), an upgraded platform with
zero `ChatEnabled` rows behaves exactly as it did before. A backfill would be a one-way data move for no
behavioural gain.

**No `MaintenanceController.CurrentFormat` bump.** Nullable and defaulted additions are
forward-restore-safe: an older backup restores its columns and the migration adds these on top.

**`UserSettings.ChatModelId` must not join the delete guard** in `LlmModelsController.Delete`. That guard
refuses to delete a model while an assignment or the platform default points at it. Extending it to user
picks would make a model undeletable because one user once chose it. `SET NULL` is the wanted behaviour
here - those users fall back to the default - which is the one case where EF nulling a tracked nullable FK
ahead of the DELETE works in our favour rather than against us.

### API

**Admin, `/api/admin/llm-models`, policy `ManagePlatform`:**

- `LlmModelDto` and `LlmModelUpsert` gain `DisplayName` (nullable) and `ChatEnabled`.
- New `PUT /api/admin/llm-models/{id}/chat-enabled` with body `{ enabled }`. A dedicated toggle so a
  checkbox click does not round-trip the whole model, which would drag the `apiKey` tri-state
  (null keeps / empty clears / value replaces) through a control that has nothing to do with keys.

**Chat, any authenticated user:**

- New `GET /api/chat/models` returning `[{ id, label, name, contextLength, isDefault }]`: the chat default
  plus every `ChatEnabled` model, the default first and the rest by label. **It never returns `apiBase` or
  any key.** The existing `listModels` is admin-only precisely because it exposes every configured
  endpoint, so the picker cannot reuse it.
- `ChatStreamRequest` gains `Guid? ModelId`.
- `ILlmSettingsResolver.ResolveAsync` gains a `Guid? modelOverride` parameter. The override is honoured
  **only** when the kind resolves to `LlmCallGroup.Chat` *and* the model is `ChatEnabled` or is the chat
  default; otherwise it is ignored and normal resolution proceeds. **This check is the security boundary** -
  without it any signed-in user could route their chat at any endpoint the platform has configured. It
  lives in the resolver rather than in `ChatController` so that no future caller can skip it.
- `IChatContextResolver.ResolveContextWindowAsync` takes the same override, so the dial reports the window
  of the model that actually served the turn. The two resolvers stay deliberately separate (one returns a
  character budget, the other a raw token window) but must agree on which model they are talking about.
- `ChatTitle` never passes an override (decision 4).
- `UserSettingsDto` gains `chatModelId` (read/write; null clears the pick). Its existing `chatModel` and
  `contextWindow` fields now resolve through the user's pick, since they seed the dial before the first turn.
- `SavedChatContextDto` gains `Guid? ModelId`.

**Un-ticking is not deleting.** `ON DELETE SET NULL` only covers a model that is removed. An administrator
who merely un-ticks `In chat` leaves every `UserSettings.ChatModelId` and saved-conversation `modelId`
still pointing at it. Both the resolver and `GET /api/chat/models` therefore treat "not offered" exactly
as they treat "not found": the override is ignored and the chat default serves the turn. Nothing is
rewritten in the database - re-ticking the model restores everyone's pick, which a cleanup pass would have
destroyed.

### Web

**Routing grid (`RoutingMatrix.tsx`).** The model column shows `Label` in bold; when a display name is set,
the slug moves into the subtitle line alongside `apiBase` and ctx. A new final **In chat** checkbox column
follows `Chat`; the grid template goes from `repeat(7,86px)` to `repeat(7,86px)_76px`. The chat default's
checkbox renders ticked and disabled with a tooltip explaining why. The `No model` row has a blank cell
there. The footer guidance changes from "One dot per column - a call type runs on exactly one model." to
"One dot per column - each call type runs on exactly one model. Chat also offers every model ticked under
In chat."

**Model editor drawer.** A `Display name` text input under `Name`, placeholder showing the slug, empty
meaning "use the slug".

**Chat picker.** A new `ChatModelPicker` component: a purple sparkle icon button in the chat toolbar,
immediately left of the `ContextDial`, with a dropdown beneath it reusing the saved-conversations menu
pattern (`relative` wrapper, `absolute top-full` menu). The existing outside-click and Escape effect in
`ChatPanel` gains a third ref rather than growing a second effect. Each row shows the **label**, followed
by the context length in smaller bracketed text, e.g. `QWEN 3.8  (200,000 ctx)`.

Selecting a model:

- updates `dialTotal` and `dialModel` immediately from that model's own `contextLength` and label, rather
  than waiting for the next turn's `meta` event;
- persists `chatModelId` to user settings;
- takes effect on the next `POST /api/chat/stream`, which carries `modelId`.

**The dial must not flip back to the slug.** `ChatPanel` derives `dialModel` from `usage.model`, and the
stream's `meta` and `done` events carry `cfg.Model` - the slug the endpoint needs, not the label. Left
alone, the dial would show the label the moment a model is picked and then revert to the slug as soon as
the first turn's `meta` arrived. The client therefore maps `usage.model` back through the offered list to
a label, falling back to the raw string when no model matches (an environment-fallback config, which has
no row and so no label). The wire format is unchanged.

The picker is disabled while `streaming`. **Mid-conversation switching needs nothing else on the wire:**
chat is already stateless per turn and resends the full history every time, so the new model receives the
previous turns as a matter of course.

Saving a conversation writes `modelId` into its context; reopening restores it, falling back to the
default when that model is no longer offered.

New i18n keys in `chat.json` and `account.json` across en/de/es/fr. Plain hyphens only, no em or en dashes.

### Tests

Red first, each mutation-verified against the real failure output.

**Unit (`Diariz.Api.Tests`, no Docker):**

- Resolver honours the override for `ChatMessage` on a `ChatEnabled` model.
- Resolver ignores the override for a model that is neither `ChatEnabled` nor the chat default.
- Resolver ignores the override for `ChatTitle` and for `Summarize`.
- `ChatContextResolver` window follows the override.
- `GET /api/chat/models` returns the default plus enabled models, and never emits `apiBase` or a key.
- `chat-enabled` toggle; `DisplayName` round-trip and the `Label` fallback when blank.

**Integration (`Diariz.Api.IntegrationTests`, Docker):**

- The migration applies.
- Deleting a model that a user has selected succeeds and nulls `UserSettings.ChatModelId`, rather than
  being refused. The in-memory provider does not enforce FKs, so this can only be proved here.

**Web (vitest + RTL, plain assertions - this repo has no jest-dom):**

- `RoutingMatrix`: the In-chat column renders; the chat default's checkbox is checked and disabled;
  toggling another model calls the handler with the right id; the label is shown when set with the slug
  in the subtitle.
- `ChatModelPicker`: the dropdown lists label plus bracketed ctx; selecting fires with the model id.
- `ChatPanel`: picking a model updates the dial total *before* any turn is sent; the next `chatStream`
  call carries `modelId`; the picker is disabled while streaming.

### Release checklist

`version.json` and its four mirrors to `0.231.0`; `RELEASES[0]`; the `CAPABILITIES` row; the README
Features row; the `docs/features.md` bullet; `Overall_Synopsis_of_Platform.md` (the new endpoint and the
`ModelId` contract); `Data_Schema.md` (three columns, the FK cascade, a migration-history row); and the
help articles `chat-over-transcripts.md` and `ai-model-settings.md`, whose relied-on behaviour changes.

**Deployment surface: server redeploy only.** Nothing under `apps/desktop/src`.

## PR 2 - add all from an endpoint (`0.232.0`)

Split out because it is the piece most likely to attract review discussion, and it has no dependency on
PR 1 in either direction: it creates rows whose `DisplayName` is null, which reads as the slug whether or
not PR 1 has landed.

### API

`POST /api/admin/llm-models/discover` with `{ apiBase, apiKey? }`, returning
`[{ id, contextLength, kind, alreadyExists }]`:

- Tries LM Studio's `/api/v0/models` first, which reports `type` and `max_context_length`; falls back to
  the OpenAI-compatible `/models`, which reports neither.
- Non-LLM filtering uses `type` when present. Otherwise an id heuristic: `embed`, `text-embedding`,
  `nomic-embed`, `bge`, `rerank`, `whisper`, `tts`, `clip`. VLMs are **kept** - they are chat models.
- `alreadyExists` is matched on `Name`, which is already globally unique.
- A model whose context length is not reported comes back null and imports at **16384** (decision 7),
  stated in the import summary so the administrator knows to correct it. That number drives both the dial
  and the real context budget, so a wrong one silently truncates.

`POST /api/admin/llm-models/discover/import` with `{ apiBase, apiKey?, names[] }` creates the rows in one
transaction.

### SSRF

This is the first endpoint that fetches an administrator-supplied URL server-side, which is exactly what
`POST /{id}/test` was written to avoid - that route takes parameters but never credentials or a URL,
because accepting one would make it a way to reach arbitrary hosts with an admin session and no model row
as an audit trail. The relaxation is deliberate and was chosen explicitly; it is bounded by:

- `ManagePlatform` policy only;
- a 10 second timeout;
- redirects not followed;
- a capped response size;
- **only parsed model ids returned** - the raw response body is never echoed back to the caller, so the
  endpoint cannot be used as a general-purpose fetch.

The reasoning goes in the endpoint's own doc comment, so it does not read later as an oversight of the
rule the neighbouring endpoint states.

### Web

An `Add all` button beside `Add model`, opening a dialog that takes a base URL and optional key. Pressing
Discover lists what was found with checkboxes pre-ticked; models already defined are shown unticked and
disabled. The button then reads `Add N models`.

> **Open call, decided at implementation time.** The preview step is a deviation from a literal one-press
> "Add All". Pointing the feature at a server with forty loaded models and no confirmation is a lot to undo
> by hand, so the preview is the default. The alternative is one press plus a summary toast
> ("Added 6, skipped 3 already defined, ignored 2 non-LLM").

### Tests

The filter is extracted as a **pure** function over parsed entries - covering both the `type` field and
the id heuristic - so it is testable without HTTP, the same separation the worker uses for
`_shape_segments`. Plus: the import summary counts; `alreadyExists` marking; a dialog test.

### Release checklist

`0.232.0` across `version.json` and its four mirrors; `RELEASES[0]`; the README Features row and the
`docs/features.md` bullet; `Overall_Synopsis_of_Platform.md` for the new outbound call; and
`ai-model-settings.md`. No schema change, so no `Data_Schema.md` edit.

**Deployment surface: server redeploy only.**
