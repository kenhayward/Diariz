# LLM usage logging + platform-admin viewer

Date: 2026-08-16
Status: approved, not yet implemented

## Problem

There is no durable record of what the platform's LLM endpoints are being asked to do. A Platform
Administrator cannot answer: which users drive the model load, which call types are expensive, whether a
model got slower after a swap, or how often calls fail. The only instrumentation today is
`LlmTelemetryHandler`, which emits Sentry/GlitchTip spans - and GlitchTip persists no span-level data at all,
so even the token counts it parses are discarded on ingest (see the comment on `LlmSpanDescription`).

Two consequences worth naming, because they shape the design:

- **Chat and formula runs are unmeasured.** They stream (SSE), and the handler deliberately does not buffer
  streaming bodies, so no `usage` block is ever read for them.
- **Chat durations are currently wrong.** `ChatStreamClient` sends with `HttpCompletionOption.ResponseHeadersRead`,
  and the handler finishes its span when `SendAsync` returns - which for a streamed call is time-to-headers,
  not the duration of the call. This spec fixes that as a side effect of measuring streams properly.

## Goal

Persist one row per outbound LLM call, attributed to its caller and purpose, and give the Platform
Administrator a viewer with filtering, sorting, per-column totals, a group-by roll-up, and deletion.

## Non-goals

- **Prompt or completion content is never stored.** Only counts and sizes. This is a hard line, consistent
  with `SentryScrubber` and the existing comment on `SentryLlmTrace.SetUsage`: meeting content stays out of
  telemetry.
- No cost/currency modelling. Every endpoint in use is self-hosted, so a dollar figure would be fiction. The
  schema does not preclude adding a per-model rate later.
- No changes to Sentry/GlitchTip reporting. The existing span behaviour is kept as-is alongside the new sink.
- No per-user or non-admin view of usage. Platform Administrator only.

## Call types

Thirteen, all of which already route through `AddLlmClient` and therefore through the handler:

| Kind | Enum | Origin |
|---|---|---|
| Recording summary | `Summarize` | `SummarizationProcessor` |
| Folder summary | `SectionSummary` | `SectionSummaryProcessor` |
| Meeting minutes | `MeetingMinutes` | `MeetingMinutesProcessor` |
| Folder minutes | `SectionMinutes` | `SectionMinutesProcessor` |
| Template minutes | `MeetingTypeMinutes` | `MeetingTypeMinutesGenerator` (fans out per section under `PerSectionMinutesStrategy`) |
| Action extraction | `ExtractActions` | `ActionsProcessor`, `RecordingActionsController` |
| Tag extraction | `Tags` | `TagsProcessor` |
| Translation | `Translation` | `RecordingTranslationController` |
| Dictation | `Dictation` | `ChatController` |
| Embedding (indexing) | `Embedding` | `EmbeddingProcessor` |
| Embedding (search query) | `SearchQuery` | `TranscriptSearch` |
| Chat message | `ChatMessage` | `ChatController` via `ChatToolOrchestrator` (loops to `MaxRounds`) |
| Formula run | `FormulaRun` | `FormulaRunProcessor` |
| Unattributed | `Unknown` | Any call made with no scope active |

`SearchQuery` is separated from `Embedding` because it is user-interactive and high-frequency, and folding it
into indexing volume would hide both.

## Data model

New entity `LlmCall`, table `LlmCalls`. One row per outbound HTTP call to a model endpoint.

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `OperationId` | `Guid` | Groups the calls of one user-facing operation |
| `Sequence` | `int` | 1-based index within the operation. Turns = `MAX(Sequence)` per operation |
| `Kind` | `int` (enum) | See table above. **Append only, never renumber** - same rule as `RecordingSource` |
| `UserId` | `Guid?` | FK to `AspNetUsers`, `ON DELETE SET NULL` |
| `UserEmail` | `string` | Snapshot taken at call time; survives user deletion |
| `RecordingId` | `Guid?` | FK, `ON DELETE SET NULL` |
| `RecordingTitle` | `string?` | Snapshot; keeps the row readable after the recording is gone |
| `SectionId` | `Guid?` | FK, `ON DELETE SET NULL`. Folder-level calls have no recording |
| `SectionName` | `string?` | Snapshot, same reasoning |
| `Model` | `string` | Exactly as sent in the request body |
| `Endpoint` | `string` | `scheme://host/path` only. The query string is dropped outright, not scrubbed - the same rule the handler already applies to span descriptions |
| `StartedAt` | `timestamptz` | |
| `CompletedAt` | `timestamptz` | |
| `DurationMs` | `int` | Stored, not derived, so ordering and `SUM` are trivial |
| `TimeToFirstTokenMs` | `int?` | Streaming calls only |
| `PromptTokens` | `int?` | |
| `CompletionTokens` | `int?` | |
| `ReasoningTokens` | `int?` | From `usage.completion_tokens_details.reasoning_tokens` where reported |
| `TotalTokens` | `int?` | Server-reported, else derived from the two halves (existing `LlmUsageParser` behaviour) |
| `PromptChars` | `int?` | Serialized request body length. Shows when `LlmContextBudget` truncation is biting |
| `Streamed` | `bool` | |
| `Success` | `bool` | |
| `StatusCode` | `int?` | Null when the call failed before a response |
| `ErrorKind` | `string?` | A class, never a message body: `Timeout`, `Canceled`, `Http500`, `Transport` |

All `DateTimeOffset` values are `.ToUniversalTime()` before saving. Npgsql throws at `SaveChanges` on a
non-zero-offset value bound to `timestamptz`, and the in-memory provider will not catch it.

**Tokens/second is derived, never stored.** A stored copy can drift from the columns it is computed from, and
the aggregate must be `SUM(CompletionTokens) / SUM(DurationMs)` - not an average of per-row rates, which lets
a 3-token 40 ms call outweigh a 4,000-token run.

**Indexes:** `(StartedAt DESC)`, `(UserId, StartedAt DESC)`, `(OperationId)`. Only three, deliberately: this is
a write-heavy table and every index is paid on every call.

**`PlatformSettings` gains three fields:**

| Field | Default | Purpose |
|---|---|---|
| `LlmUsageLoggingEnabled` | `true` | Master switch; when off the handler skips the channel entirely |
| `LlmUsageRetentionDays` | `90` | `0` = keep forever |
| `LlmStreamUsageEnabled` | `true` | Sends `stream_options: {"include_usage": true}` on streaming requests |

The migration is purely additive (one new table, three new nullable-or-defaulted columns), so it is
forward-restore-safe and `MaintenanceController.CurrentFormat` is **not** bumped.

## Capture

### `LlmCallScope` (AsyncLocal)

Ambient context carrying `Kind`, `UserId`/`UserEmail`, `RecordingId`/`RecordingTitle`, `SectionId`/`SectionName`,
a fresh `OperationId`, and a counter supplying `Sequence`.

Pushed once at the top of each job, twelve sites: `SummarizationProcessor`, `SectionSummaryProcessor`,
`MeetingMinutesProcessor`, `SectionMinutesProcessor`, `ActionsProcessor`, `TagsProcessor`, `EmbeddingProcessor`,
`FormulaRunProcessor`, `TranscriptSearch`, `RecordingTranslationController`, `RecordingActionsController` (the
synchronous re-extract path), and `ChatController` (which pushes for both chat and dictation).

`MeetingTypeMinutesGenerator` and `ChatToolOrchestrator` push **nothing**. Their inner calls land in the
enclosing scope and increment `Sequence`, which is exactly how per-section fan-out and chat tool-loop turns get
counted.

A call made with no scope active is still logged, as `Kind = Unknown`. This is deliberate: an LLM client added
later shows up as a visible unattributed row rather than silently vanishing - the same failure mode
`AddLlmClient` was created to prevent.

### Handler

`LlmTelemetryHandler` keeps its Sentry span and gains a second output. It stamps `StartedAt`, measures duration,
records status, parses usage, and takes `PromptChars` from the serialized request body length - so no call site
has to report prompt size.

`LlmUsageParser` is extended to read `usage.completion_tokens_details.reasoning_tokens`. It keeps its existing
contract: best-effort, never throws, a missing count never costs the caller its timing.

### Streaming

For a `text/event-stream` response the handler wraps the response content in a pass-through `Stream` that:

- records the timestamp of the first byte as `TimeToFirstTokenMs`,
- scans `data:` payloads for a final chunk carrying `usage`,
- completes the record on end-of-stream, dispose, or fault.

This lives in the handler rather than in `ChatStreamClient` so that every streaming client - present or future -
is covered without remembering to do anything, preserving the single-instrumentation-point invariant the
existing design is built on. It also corrects the time-to-headers bug described under Problem.

When `LlmStreamUsageEnabled` is on, streaming request bodies carry `stream_options: {"include_usage": true}`.
Support for this must be verified live against LM Studio; an endpoint that rejects the field is handled by
turning the setting off, with no redeploy. With it off, streaming rows have null token counts and still carry
accurate duration and TTFT.

### Writer

The handler pushes a completed record onto a bounded `Channel<LlmCall>` (capacity 10,000, drop-oldest with a
throttled warning) and returns immediately. `LlmUsageWriter : BackgroundService` drains it and batch-inserts
every ~2 seconds or 200 rows.

The writer opens its **own DI scope per batch**. The handler must never hold a `DbContext`: it is registered
transient, but `HttpClientFactory` pools handler instances for roughly two minutes, so an injected scoped
dependency would be captive.

Rows buffered at the moment of a hard crash are lost. That is the accepted trade: a usage log must never add
latency to a summary, and a database problem must never degrade transcription or chat.

### Retention

`LlmUsageRetentionDays` is swept nightly, reusing the `AudioRetentionWorker` pattern (pure schedule helper +
hosted service). `0` disables the sweep.

## API

`LlmUsageController` at `/api/admin/llm-usage`, `[Authorize(Policy = "ManagePlatform")]`. Not
`ReadAdminSettings`, which Administrators also hold - this is Platform Administrator only.

One shared filter binds to every endpoint: `from`, `to`, `userIds[]`, `kinds[]`, `models[]`, `outcome`
(`all`/`ok`/`failed`), `recordingId`, `sectionId`. **`from` defaults to 30 days ago**, so no query is ever
unbounded.

| Endpoint | Returns |
|---|---|
| `GET ?mode=operations` (default) | One row per `OperationId`: kind, user, model, target, turns (`COUNT(*)`), summed tokens, wall clock `MIN(StartedAt)`-`MAX(CompletedAt)`, and outcome (failed if **any** call in the operation failed) |
| `GET ?mode=calls` | Flat per-call rows; pass `operationId` to expand one operation |
| `GET /summary?groupBy=user,model,kind` | Multi-select group-by roll-up over the same filter |
| `DELETE /` | Deletes everything matching the filter via `ExecuteDeleteAsync`; returns the count |
| `GET /filters` | Distinct users, models and kinds present, for the dropdowns (model is free text) |

Totals are computed **over the whole filter set, not the returned page** - a separate aggregate query, not a
sum of the rows on screen.

Aggregation rules:

- Tokens/second is `SUM(CompletionTokens) / SUM(DurationMs)`.
- **Nulls are not zeros.** Sums ignore nulls, and each token column also reports "measured on N of M calls", so
  a total is never read as complete when some endpoint reported no usage.
- Turns are per operation: the summary exposes average and maximum, not a sum.

Sorting uses a whitelist of column names mapped to expressions; no user-supplied string reaches SQL. Paging
defaults to 50 rows and caps at 200.

The response carries `recordingId`/`sectionId` plus the title snapshots. Links are rendered unconditionally: a
Platform Administrator has no automatic access to another user's recording, so some will 403, and pre-checking
access per row costs more than it is worth.

## UI

New route `/admin/llm-usage`, gated client-side on the `ManagePlatform` permission and linked from the account
menu beside Settings. A dedicated page rather than a `SettingsModal` tab: the modal is held at a fixed height
for tab-flipping, which a twelve-column table with filters and totals cannot use well.

- **Filter bar:** date presets (24 h / 7 d / 30 d / custom), user / type / model multi-selects, outcome toggle.
- **Mode tabs:** Operations, Calls, Summary.
- **Table:** sortable headers, sticky totals row, pagination. Operations rows expand into their calls.
- **Summary:** group-by chips (User / Model / Type) over the same filter and totals.
- **Delete matching:** destructive action; the confirm dialog states the exact row count before proceeding.

Recording and folder links are built through `useRoomBasePath` - a link built without it silently drops the
user into their Personal Room. All strings go through i18n. Plain hyphens only in user-facing text.

## Testing

| Layer | Covers |
|---|---|
| .NET unit | `LlmCallScope` nesting and `Sequence` increments; `LlmUsageParser` reasoning tokens; the SSE usage scan; the sort whitelist rejecting unknown columns; the retention schedule helper |
| .NET integration (Docker) | Every aggregate query; `ON DELETE SET NULL` for user/recording/section; `ExecuteDeleteAsync` under a filter; the `timestamptz` round-trip |
| Web (vitest + RTL) | Filters produce the expected API params; totals row renders; sort toggles; delete confirmation |
| Live | The `stream_options` toggle against LM Studio |

The totals aggregator is extracted as a **pure function** so it can be tested without a database - the same
separation as `pipeline._shape_segments` in the worker.

Aggregate behaviour is tested at the integration layer, not the unit layer. The in-memory provider does not
translate `GROUP BY`/`SUM` faithfully, so a unit test of totals would pass without proving anything.

Web tests use plain assertions; `jest-dom` matchers are not used anywhere in `apps/web`.

## Delivery

Three PRs, each shippable on its own:

| PR | Contents |
|---|---|
| 1 | Entity, migration, `LlmCallScope` and its push sites, non-streaming handler capture, channel writer, platform settings, retention sweep |
| 2 | SSE stream wrapper, `stream_options`, time-to-first-token |
| 3 | API endpoints, admin page, i18n |

Each PR carries its own version bump and release-notes entry, all three being functional enhancements (minor
+1, build reset): PR 1 is `0.216.0`, PR 2 `0.217.0`, PR 3 `0.218.0` - assuming nothing else merges between
them, since the bump is computed from `version.json` at the time the branch is cut. Docs updated in lockstep: `Data_Schema.md` (PR 1), `Overall_Synopsis_of_Platform.md` (PRs 1
and 2 for the capture contract, PR 3 for the admin surface), README Features + `docs/features.md` +
`CAPABILITIES` (PR 3, when the capability becomes user-visible).

**Deployment surface: server redeploy only.** Nothing under `apps/desktop` is touched, so no desktop release is
required for any of the three.

## Risks

| Risk | Mitigation |
|---|---|
| LM Studio rejects or ignores `stream_options` | `LlmStreamUsageEnabled` toggles it off without a redeploy; rows keep duration and TTFT |
| Table growth (embeddings write a row per chunk) | 90-day default retention, nightly sweep, 30-day default filter window, only three indexes |
| Aggregate queries slow as the table grows | Every query is date-bounded by default and covered by the `StartedAt` index |
| Stream wrapper breaks the chat hot path | Pass-through only, never buffers; end-to-end verification in the browser pane before merge |
| Scope not pushed at some site, so rows read `Unknown` | Visible in the viewer by design; an integration test asserts each processor's kind |
