# Test an AI model against a real recording

Design, 2026-08-20.

## Problem

The model editor's test rail (`/admin/llm-models`, drawer, right-hand column) proves an endpoint answers.
It cannot tell you whether a model is any good at the job it has been assigned.

Today `LlmTestProbe` sends one fixed prompt - "Summarise this meeting excerpt in one sentence" - over a
hard-coded three-line fake transcript, identically for every tab. That was deliberate: it keeps
time-to-first-token comparable between models. But it means an administrator pointing the Tags group at a
new model has no way to see whether that model returns usable tags, and finds out only when real
recordings come back with bad output.

## What we are building

On the three content tabs - **Tags, Actions and Summaries** - the test call runs the **real prompt for that
call group** against a **real recording of the administrator's own**, and the panel shows the **parsed
result** the pipeline would have produced.

The chosen recording is remembered per administrator, so the three tabs and successive models are all
tested against the same content.

`ModelBase`, `MinutesAndFormulas`, `Translation` and `Chat` are unchanged and keep the fixed sample.

## Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Fixed sample vs recording | Recording **replaces** the fixed sample on the three content tabs | One button, one code path. The fixed sample survives only where there is no single real prompt. |
| Tab coverage | Tags, Actions, Summaries | Each is exactly one chat call built by a pure prompt builder. The other four are not (see Out of scope). |
| Output | Parsed, with raw text underneath | A parse failure is the single most useful test result: it is exactly what breaks the real pipeline silently. |
| Remembering | Per administrator, server-side, one recording shared by all three tabs | Survives browser and machine changes; one recording keeps timings comparable between models. |
| Picker scope | The administrator's own recordings only | A platform admin must not send another user's transcript to a third-party endpoint under test, nor see its output in their browser. |
| Streaming | Keep `stream: true` | TTFT is the panel's headline number and the thing that separates "slow model" from "model still loading". |
| Picker contents | Only recordings that have a transcript | A recording with no segments cannot be tested; offering it only produces an error. |

### Accepted trade-offs

- **TTFT is no longer comparable across models unless the same recording is remembered.** This is why
  remembering it is part of the feature rather than a nicety, and why the selection is shared by all three
  tabs rather than per tab.
- **The three content tabs stream while their real pipeline clients do not.** The prompt and the returned
  text are identical; only the transport flag differs. Where an endpoint genuinely behaves differently
  under `stream: true`, surfacing that is useful rather than misleading.
- **A real transcript is sent to the endpoint under test.** The model's output is returned to the browser
  and, as today, never persisted - the usage log records counts only. The UI states this.

## Architecture

### Server

**The probe stays transport.** `LlmTestProbe` measures, streams and reports; it stops owning a prompt.

- `ILlmTestProbe.RunAsync` gains `IReadOnlyList<ChatMessage> messages` and a `maxResponseChars` argument.
- The existing fixed prompt moves out of the probe into `LlmTestSample` (a `ChatMessage[]` constant),
  still used by the four unchanged groups.
- `MaxResponseChars` stops being a constant: 8,000 for the fixed sample, 16,000 for recording-backed
  groups. The Actions prompt asks the model to reason in prose *before* emitting its JSON array, so a long
  meeting legitimately exceeds 8,000 characters - and truncating it would produce a parse failure the real
  pipeline would never hit.

**`LlmTestPromptFactory`** (new, `Services/Llm`) turns a `LlmCallGroup` plus recording id into the messages
the real pipeline would build, using the same pure builders and the same `IPromptTemplateProvider`
templates:

| Group | Builder | Template |
|---|---|---|
| Tags | `TagsPrompt.BuildMessages` | `tagcloud` |
| Actions | `ActionsPrompt.BuildMessages`, meeting date = `StartedAt ?? CreatedAt` | `extract-actions` |
| Summaries | `SummarizationPrompt.BuildMessages`, `needName` = the recording has no `Name` | `summarise` |

`Summaries` uses the recording's real `needName` rather than always asking for a title: the test's job is
to show what the pipeline would do for *this* recording.

**Parsing runs server-side, through the pipeline's own parsers**, so a parse failure in the test is the
failure the pipeline would hit. This needs one small pure refactor, because every parser currently takes
the **whole `/chat/completions` response JSON** while the streaming probe only accumulates content deltas:

- `TagsPrompt`, `ActionsPrompt` and `SummarizationPrompt` each grow a `ParseContent(string content, ...)`;
  the existing `ParseResponse(string responseJson, ...)` becomes a thin wrapper that pulls
  `choices[0].message.content` out and delegates. Pure extraction, no behaviour change, and it keeps one
  parsing implementation rather than two.

`LlmTestOutcome` gains two fields:

- `ParsedKind` - `"Tags" | "Actions" | "Summary" | null` (null for the fixed-sample groups)
- `ParsedJson` - the parsed structure, serialised

**There is no `ParseError`.** All three parsers are total: they never throw on bad content. `TagsPrompt`
and `ActionsPrompt` return an **empty list** when the array is missing or malformed, and
`SummarizationPrompt` falls through to treating the whole reply as plain text. That is the real signal and
it is more useful than an exception would be - an empty list is precisely what the pipeline would have
stored. The UI says "the pipeline would have extracted nothing from this" rather than "parsing failed".

**Segment loading.** The `Speaker.DisplayName` lookup into `SegmentDto` is copy-pasted across ten call
sites. The factory needs it, so it gets extracted once into a shared helper. The other nine sites are left
alone - retrofitting them is not this feature's job.

**`POST /api/admin/llm-models/{id}/test`** gains `RecordingId`:

- 400 when the group is one of the three and no recording is given.
- 404 when the recording does not exist **or is not owned by the caller**. A platform admin holds
  `ManagePlatform` and could otherwise reach any row; ownership is checked against the JWT's `UserId`.
- 400 with a clear message when the recording's latest transcription has no segments.
- The existing `LlmCallScope.Push(LlmCallKind.AdminTest, ...)` gains the recording id and name, so the
  usage log shows which recording each test ran against.

### Remembering the selection

New nullable column `UserSettings.LlmTestRecordingId` (`Guid?`) plus migration. Forward-restore-safe (an
added nullable column), so `MaintenanceController.CurrentFormat` is untouched.

Two endpoints on the admin controller, alongside the existing `defaults` and `assignments`:

- `GET /api/admin/llm-models/test-recording` returns `{ recordingId, title }`, both null when unset or
  when the remembered recording has since been deleted.
- `PUT /api/admin/llm-models/test-recording` takes `{ recordingId }` - null clears.

Deliberately **not** added to `api/user/settings`: that DTO is documented as the user's own AI settings and
feeds the n8n generated client, and this is admin-panel state.

### Web

**`RecordingPicker`** (new, `components/llmmodels`) - a searchable dropdown over `api.listRecordings()`,
filtered to statuses that have a transcript (`Transcribed`, `Summarized`, `Summarizing`, `Merging`), each
row showing name, date and duration.

**`TestRail`** gains a "Test against" row below its header:

- On the three content tabs: the picker, pre-filled from the remembered recording, plus a one-line note
  that the transcript is sent to the endpoint under test. Choosing a recording PUTs it immediately.
- On the other four tabs: static "Built-in sample transcript" text. Behaviour unchanged.
- Before a recording is chosen, Run is disabled with "Choose a recording first".

**`TestResultCard`** renders parsed-first, raw text in a collapsed block underneath:

| Kind | Rendering |
|---|---|
| Tags | chips with weights |
| Actions | read-only table: text / actor / deadline |
| Summary | title line (when the model was asked for one) plus the paragraph |
| Tags/Actions, empty | a plain banner saying the pipeline would have extracted nothing, raw text expanded |

Read-only renderings, purpose-built for the rail. `ActionsTable` and `TagCloud` are bound to real recording
state and mutations and are not reused.

## Testing

TDD throughout - failing test first.

**Unit (`Diariz.Api.Tests`, no Docker)**

- `ParseContent` on each of the three prompt classes: happy path, and the malformed shapes each parser
  already guards (which return empty rather than throwing). Plus a test that `ParseResponse(json)` and
  `ParseContent(content)` agree, so the extraction cannot drift.
- `LlmTestPromptFactory`: each group produces the same messages as its real builder called directly, with a
  fake template provider.
- Controller: 400 with no recording on a content tab, 404 for another user's recording, 400 for a recording
  with no segments, and that the remembered id round-trips through GET/PUT.
- The probe: given messages, it sends exactly those; the `maxResponseChars` cap is honoured. Use `SendAsync`
  with `ResponseHeadersRead` and a read-counting stream - `PostAsync` buffers above the handler chain and
  would make a streaming assertion pass vacuously.

**Integration (`Diariz.Api.IntegrationTests`, Docker)**

- The new column against real Postgres, and the ownership filter (a second user's recording is a 404).
- Segment loading for the factory against real relational ordering - the in-memory provider ignores
  ordering inside a filtered `Include`.

**Web (`vitest` plus RTL)**

- `RecordingPicker` filters out untranscribed recordings and calls the PUT on selection.
- `TestRail` shows the picker on the three content tabs and the static text on the other four; Run is
  disabled with no recording.
- `TestResultCard` renders each parsed kind, and the extracted-nothing banner with the raw text expanded.
- Use `userEvent`, not `fireEvent`: `fireEvent` fires handlers on disabled controls, so the disabled-Run
  assertion would pass for a reason the browser never reproduces.

**Verification.** Layout and overflow in the rail are checked in the running app with real measurements,
not by asserting class names - jsdom computes no geometry.

## Release checklist

One PR. Server redeploy only - nothing under `apps/desktop`.

1. `version.json` plus its four mirrors. Functional enhancement: minor +1, build reset to 0.
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`.
3. `CAPABILITIES` table row - the model test panel's description changes.
4. README Features table row.
5. `docs/features.md` bullet, in lockstep with the README row.
6. `docs/Overall_Synopsis_of_Platform.md` - two new admin endpoints and the test path's new dependency on
   recording content.
7. `docs/Data_Schema.md` - the new `UserSettings.LlmTestRecordingId` column and its migration row.

Help content (`apps/web/src/content/help/**`) is reviewed but not automatically updated: it changes only if
the behaviour a user relies on changes, and this is admin-facing.

## Out of scope

- **MinutesAndFormulas.** Not one call: `MeetingTypeMinutesGenerator` resolves a meeting-type template,
  builds a formula context, optionally makes a separate notes-enhancement call, then hands off to a
  strategy that may generate section by section. Reproducing even its single-call shape needs two
  extractions on the most intricate code in the app - the message assembly is inside
  `SingleCallMinutesStrategy`, and `ResolveTemplateAsync`, `ResolvedTemplate`, `PrimaryContext` and
  `ResolveField` are all private to the generator. Deferred to its own PR.
- **Translation and Chat** - need a target language and a question respectively, which a recording does not
  supply.
- **ModelBase** - a parameter scope, not a call type; nothing is ever dispatched to it.
- Testing against another user's recording.
- A per-tab remembered recording.
- Retrofitting the shared segment-mapping helper across the other nine call sites.
