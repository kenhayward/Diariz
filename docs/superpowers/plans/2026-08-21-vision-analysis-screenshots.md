# Vision Analysis for Screenshots Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user drag a screenshot from a recording's Notes tab into the chat prompt box and ask a vision-capable model about it, with the send blocked when the selected model cannot see images.

**Architecture:** The chat request carries `{recordingId, screenshotId}` references, never image bytes. The API re-checks read access, loads the PNG from MinIO, rescales it to fit inside 1920x1080 (SkiaSharp, JPEG q92) or passes it through untouched when it is already small enough, and attaches it to the last user message as an OpenAI `image_url` content part. The existing but so-far-unread `images_supported` model parameter becomes the gate on both sides, exposed to the picker through `ChatModelCatalog` so the picker and the settings resolver compute it from one shared parameter-layer stack.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core + Postgres; SkiaSharp; React 19 + TS + Vite + Tailwind v4; xUnit (unit + Testcontainers integration); vitest + @testing-library/react.

**Design spec:** [`docs/superpowers/specs/2026-08-21-vision-analysis-screenshots-design.md`](../specs/2026-08-21-vision-analysis-screenshots-design.md)

## Global Constraints

Every task's requirements implicitly include this section.

- **TDD is mandatory.** Write the failing test, run it, watch it fail for the *stated reason*, then implement. No production code without a preceding failing test.
- **Mutation-verify every test.** After a test goes green, break the implementation on purpose, re-run, confirm the test fails, restore. This repo's dominant defect class is tests that cannot fail. When restoring a mutated `.cs` file, **edit it back in place** - restoring from a copy preserves the old mtime and MSBuild skips the rebuild, so you keep testing the mutated binary.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only, in UI strings, i18n catalogs (`apps/web/src/locales/**`), release notes and help articles. Code comments and internal docs are unaffected. Do not verify this by piping `git diff` into python on Windows - the pipe decodes cp1252 and reports a false zero. Write to a file and decode UTF-8 explicitly.
- **Never `git add -A` or `git add .`** in this repo - it sweeps hundreds of untracked scratch files into the commit. Stage explicit paths, always.
- **No jest-dom.** Zero of the 230+ web test files use its matchers. Use plain assertions (`expect(el.textContent).toContain(...)`, `expect(el.hasAttribute("disabled")).toBe(true)`). Do not install it or edit `src/test-setup.ts`.
- **Use `userEvent`, not `fireEvent`, for anything that must respect a disabled control.** `fireEvent.click` fires handlers on disabled elements, so a gate test written with it passes for a reason the browser never reproduces. `userEvent` is already installed.
- **`dotnet test --filter "Name=X"` does not work here** despite CLAUDE.md. Use `--filter "FullyQualifiedName~X"`.
- **Build `Diariz.slnx` before pushing.** Unit-only runs miss compile breaks in the integration project and in CodeQL. Controller constructor changes have a second construction site in `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs` - `ChatController` gains a constructor parameter in Task 8, so expect to touch it.
- **Do not add `.AsSplitQuery()`.** Split queries are the app-wide default since 0.228.4.
- **Never commit or push to `main`.** Work on `feat/vision-analysis-screenshots` (already created). Finish by pushing and opening a PR.
- **No `InternalsVisibleTo`.** Tests reach internal helpers through public seams with `TestSupport` fakes. Do not widen visibility.
- **Web tests that `vi.mock("../lib/api")` list every method explicitly.** `ChatPanel` gains no new api method in this plan, but any test that renders a child which calls one must have it in the mock factory or the test fails with "not a function".
- Version: **`0.238.0`** (feature: Minor +1, Build reset). Bump `version.json` plus all four mirrors.
- Deployment surface: **server redeploy only**. No desktop shell files change.

---

# File structure

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Services/VisionImageBounds.cs` | **New.** Pure fit arithmetic. No Skia, no storage |
| `src/Diariz.Api/Services/VisionImageEncoder.cs` | **New.** `IVisionImageEncoder` - load, decide, rescale, base64 |
| `src/Diariz.Api/Services/Llm/LlmParameterStack.cs` | **New.** The shared layer stack, extracted from the resolver |
| `src/Diariz.Api/Services/Llm/LlmSettingsResolver.cs` | Calls the shared stack instead of building its own |
| `src/Diariz.Api/Services/Llm/ChatModelCatalog.cs` | Resolves `SupportsImages` per model from the same stack |
| `src/Diariz.Api/Services/SummarizationPrompt.cs` | `ChatMessage` gains `ImageDataUrls` |
| `src/Diariz.Api/Services/ChatContextBuilder.cs` | Attaches images to the last user message |
| `src/Diariz.Api/Services/ChatToolOrchestrator.cs` | Emits a parts array when images are present |
| `src/Diariz.Api/Services/ChatContextMeter.cs` | `EstimateImageTokens` |
| `src/Diariz.Api/Controllers/ChatController.cs` | Validation order, encoding, meter |
| `src/Diariz.Api/Controllers/ChatModelsController.cs` | Passes `SupportsImages` through |
| `src/Diariz.Api/Contracts/ApiDtos.cs` | `ChatScreenshotRefDto`, request + saved-context + model DTO fields |
| `src/Diariz.Api/Diariz.Api.csproj` | SkiaSharp package references |
| `apps/web/src/components/ScreenshotStrip.tsx` | Draggable thumbnails |
| `apps/web/src/components/ScreenshotsSection.tsx` | Hint line, passes `draggable` |
| `apps/web/src/components/ChatScreenshotTray.tsx` | **New.** Tray of thumbnails with remove controls |
| `apps/web/src/components/ChatPanel.tsx` | Drop target, tray, vision gate, wire, save/load |
| `apps/web/src/components/ChatModelPicker.tsx` | Marks vision-capable rows |
| `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts` | Request field and `supportsImages` |
| `apps/web/src/locales/{en,de,es,fr}/{chat,workspace}.json` | New strings |
| `integrations/n8n-nodes-diariz/generated/index.ts` | Regenerated - `api/chat/stream` is a published route |

---

# Tasks

## Task 1 - `VisionImageBounds.Fit`

The pure arithmetic, isolated so the interesting cases are testable without Skia or MinIO.

- [ ] Write `tests/Diariz.Api.Tests/VisionImageBoundsTests.cs` covering: within bounds returns null; over on width only; over on height only; over on both; exactly 1920x1080 returns null; a 1x8000 strip clamps on height and keeps its ratio; the result is never larger than the input on either axis; a 1-pixel input does not round to zero.
- [ ] Run, confirm it fails to compile (the type does not exist).
- [ ] Implement `VisionImageBounds` with `MaxWidth = 1920`, `MaxHeight = 1080`, and `Fit(int w, int h) -> (int W, int H)?` using `scale = min(MaxWidth/w, MaxHeight/h)`, returning null when `scale >= 1`. Round to nearest, floor at 1.
- [ ] Green, then mutation-verify: change `min` to `max` and confirm the extreme-ratio test fails.

**Requirements:** never upscale; never return zero on an axis; ratio preserved to within a pixel of rounding.

## Task 2 - SkiaSharp and `IVisionImageEncoder`

- [ ] Add `SkiaSharp` and `SkiaSharp.NativeAssets.Linux.NoDependencies` to `src/Diariz.Api/Diariz.Api.csproj`. Restore. **`packages.lock.json` will need regenerating** - this repo floats versions *and* commits lock files, so a restore warning in CI is real, not a stale cache.
- [ ] Confirm `src/Diariz.Api/Dockerfile` needs no apt packages. If Skia fails to load in the container at Task 17's live check, that is where to come back to.
- [ ] Write `tests/Diariz.Api.Tests/VisionImageEncoderTests.cs` using `FakeAudioStorage` from `TestSupport`: a 3840x2160 PNG comes back as a `data:image/jpeg;base64,` URL whose decoded dimensions fit the box and whose reported `Width`/`Height` match; an 800x600 PNG comes back as `data:image/png;base64,` with base64 bytes byte-identical to what was stored and dimensions unchanged; an exactly-1920x1080 PNG takes the pass-through path.
- [ ] Run, confirm failure.
- [ ] Implement `VisionImageEncoder : IVisionImageEncoder` with `Task<VisionImage> EncodeAsync(MeetingScreenshot shot, CancellationToken ct)` returning `record VisionImage(string DataUrl, int Width, int Height)`. Decide from `shot.Width`/`shot.Height` via `VisionImageBounds.Fit`; pass-through reads `BlobKey` and emits PNG; resize decodes, resamples at high quality, encodes JPEG q92.
- [ ] Register in `Program.cs` DI.
- [ ] Green, then mutation-verify: hard-code the pass-through branch to also re-encode, and confirm the byte-identity test fails.

**Requirements:** a capture inside the box must never be decoded or re-encoded - assert byte identity, not merely "it is a PNG".

## Task 3 - Extract the shared parameter stack

This is what stops the picker and the pipeline from drifting. Refactor first, no behaviour change.

- [ ] Write `tests/Diariz.Api.Tests/LlmParameterStackTests.cs`: the stack for a model with a group override lists that override first, then `ModelBase`, then the platform layers in `LlmPlatformLayers.Below` order; a model with no override row for the group yields a null in that slot rather than omitting the layer.
- [ ] Run, confirm failure.
- [ ] Implement `LlmParameterStack.For(LlmModel? model, LlmCallGroup? group, LlmDefaultsOptions defaults, PlatformSettings? platform) -> List<string?>`, lifted verbatim from `LlmSettingsResolver.ResolveAsync`.
- [ ] Replace the inline construction in `LlmSettingsResolver.ResolveAsync` with a call to it.
- [ ] Run the **whole** existing `Llm` test surface (`--filter "FullyQualifiedName~Llm"`) and confirm it is still green. A refactor that changes a resolved parameter is a bug, not a step.

## Task 4 - `SupportsImages` on the chat model catalogue

- [ ] Extend `tests/Diariz.Api.Tests` for `ChatModelCatalog`: `SupportsImages` is true when the model's `Chat` override sets `images_supported: true`; true when only its `ModelBase` row sets it; false when neither mentions it (platform default); and a `Chat` override of `false` beats a `ModelBase` `true`.
- [ ] Add the agreement test: for the same model and the `Chat` group, `ChatModelCatalog.ListAsync(...).SupportsImages` equals `LlmSettingsResolver.ResolveAsync(ChatMessage, modelId).Parameters.ImagesSupported`. **This is the test that makes Task 3 load-bearing.**
- [ ] Run, confirm failure.
- [ ] Add `SupportsImages` to `ChatModelOption`; give `ChatModelCatalog` `IOptions<LlmDefaultsOptions>`, read `PlatformSettings`, `Include(m => m.Parameters)`, and resolve via `LlmParameterStack` + `LlmParameterLayers.Resolve`.
- [ ] Add `SupportsImages` to `ChatModelDto` and pass it through `ChatModelsController.List`.
- [ ] Green, mutation-verify by flipping the catalogue's default to `true` and confirming the "neither mentions it" test fails.

**Requirements:** the catalogue must not re-implement the layer walk. If the agreement test passes while the catalogue builds its own stack, the test is not doing its job.

## Task 5 - Images on `ChatMessage` and the orchestrator wire

- [ ] Write orchestrator tests: with `ImageDataUrls` null or empty, the shaped message is `{ role, content: "<string>" }` exactly as today; with one or more URLs, `content` is an array whose first element is `{type:"text"}` and whose remainder are `{type:"image_url", image_url:{url}}` in order.
- [ ] Write a `ChatContextBuilder.BuildMessages` test: supplied images land on the **last user message** and on no other; with no user message in the history nothing throws.
- [ ] Run, confirm failure.
- [ ] Add `IReadOnlyList<string>? ImageDataUrls = null` to `ChatMessage` (defaulted, so every existing construction site is untouched).
- [ ] Implement in `ChatContextBuilder` and `ChatToolOrchestrator`.
- [ ] Green, mutation-verify by always emitting the array form and confirming the no-images test fails.

**Requirements:** the string form must be byte-identical for every existing caller. Assert the serialised JSON, not just the object shape - and serialise it the way the pipeline does, not with a hand-rolled `JsonSerializer.Serialize` whose casing may differ.

## Task 6 - Context meter counts images

- [ ] Write `ChatContextMeter` tests: `EstimateImageTokens(1920, 1080)` is about 2,765; a small image costs proportionally less; zero images add zero.
- [ ] Run, confirm failure. Implement `ceil(width * height / 750.0)`.
- [ ] Green, mutation-verify.

## Task 7 - Wire DTOs

- [ ] Add `public record ChatScreenshotRefDto(Guid RecordingId, Guid ScreenshotId);`
- [ ] Add `IReadOnlyList<ChatScreenshotRefDto>? Screenshots = null` to `ChatStreamRequest` **and** `SavedChatContextDto`, both as trailing optional parameters so existing positional construction still compiles.
- [ ] Confirm no migration is needed: `ContextJson` is already a JSON blob and older rows read the missing key as null. Do **not** bump `MaintenanceController.CurrentFormat` - an additive key is forward-restore-safe.

## Task 8 - `ChatController.Stream` validation and encoding

- [ ] Write controller tests in `tests/Diariz.Api.Tests` using `TestDb`, `FakeAudioStorage`, `FakeJobQueue`: 400 when screenshots are attached and the resolved model has `ImagesSupported = false`, with the model named in the message; 404 when a screenshot's recording is not readable by the caller; 404 when the screenshot id does not belong to the paired recording; the happy path reaches the orchestrator with `ImageDataUrls` populated on the last user message; **no storage read happens on any of the rejection paths** (assert against the fake's call log).
- [ ] Run, confirm failure.
- [ ] Inject `IVisionImageEncoder` into `ChatController`. **This changes the constructor - update the second construction site in `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`.**
- [ ] Implement the ordered checks from the spec: access, ownership pairing, vision flag, then encode. Fold the encoder's dimensions into `promptTokens` via `EstimateImageTokens`.
- [ ] Green, mutation-verify each rejection independently.

**Requirements:** reject before reading blobs. The "no storage read on rejection" assertion is the one that pins this, and it is easy to write a version that passes while the reads happen anyway - check the fake actually records reads.

## Task 9 - Integration tests

- [ ] In `tests/Diariz.Api.IntegrationTests`, against real MinIO: store an oversized PNG, run the encoder, decode the returned data URL and assert its real dimensions fit 1920x1080 and its media type is `image/jpeg`; store a small PNG and assert `image/png` with identical bytes.
- [ ] Save a conversation whose context carries screenshot refs, reload it, assert the refs survive the `ContextJson` round trip.
- [ ] Assert a `ContextJson` written before this change (no `screenshots` key) still deserialises with `Screenshots` null.

## Task 10 - Web types and client

- [ ] Add `supportsImages: boolean` to `ChatModelOption` in `lib/types.ts`.
- [ ] Add `screenshots?: { recordingId: string; screenshotId: string }[]` to the `chatStream` body type and to the saved-context type in `lib/api.ts`.
- [ ] No test of its own - Tasks 12 and 13 assert the call payload.

## Task 11 - Draggable strip and the hint

- [ ] Write `ScreenshotStrip.test.tsx` additions: with `draggable`, a thumbnail has `draggable="true"` and its `dragStart` handler calls `dataTransfer.setData` with `application/x-diariz-screenshot` and a JSON payload carrying `recordingId`, `screenshotId`, `capturedAtMs`; without the prop, thumbnails are not draggable; clicking still calls `onOpen` in both cases.
- [ ] Write `ScreenshotsSection.test.tsx` additions: the hint text renders when there are shots; the section still renders nothing when there are none.
- [ ] Run, confirm failure.
- [ ] Implement. Add `screenshotsDragHint` to `workspace` in all four locales.
- [ ] Green, mutation-verify by dropping the MIME type to `text/plain` and confirming the payload test fails.

## Task 12 - `ChatScreenshotTray`

- [ ] Write `ChatScreenshotTray.test.tsx`: renders one thumbnail per attached shot with an accessible remove control per thumbnail; clicking a remove control calls `onRemove` with that shot's id and no other; renders nothing when the list is empty.
- [ ] Run, confirm failure. Implement, using `api.screenshotThumbUrl` for the image source.
- [ ] Green, mutation-verify.

**Requirements:** extracted rather than inlined because `ChatPanel.tsx` is already 1142 lines.

## Task 13 - `ChatPanel`: drop, gate, wire

- [ ] Write `ChatPanel.test.tsx` additions:
  - dropping a valid payload on the composer adds a thumbnail;
  - dropping the same shot twice leaves one;
  - a drop carrying only `text/plain` is ignored;
  - the tray's remove control clears it;
  - with a shot attached and a non-vision model selected, Send is disabled and "Select a vision model" renders - **via `userEvent`**, so the disabled state is genuinely exercised;
  - switching to a vision-capable model re-enables Send;
  - sending calls `api.chatStream` with the `screenshots` array;
  - saving and reloading a conversation round-trips the tray.
- [ ] Run, confirm failure.
- [ ] Implement `attachedShots` state, the drop target with a hover affordance, the tray, `blockedByVision`, the request field, and save/load. Restoring a conversation drops refs whose shot no longer resolves, silently.
- [ ] Add `selectVisionModel`, `removeScreenshot`, `dropScreenshot`, `modelSupportsImages` to `chat` in all four locales.
- [ ] Green, mutation-verify the gate by removing the `disabled` binding and confirming the `userEvent` test fails.

**Requirements:** attachments must survive navigation between recordings - do not clear them on context
change. **There is no cap on how many may be attached** - do not add a defensive limit; the context dial is
the feedback mechanism.

## Task 14 - Picker marks vision models

- [ ] Write `ChatModelPicker.test.tsx` additions: a `supportsImages` model renders the marker with an accessible label; one without does not.
- [ ] Run, confirm failure. Implement. Green, mutation-verify.

## Task 15 - Generated artefacts

`api/chat/stream` is a **published** route (only `api/admin`, `api/platform`, `api/oauth` and `api/maintenance` are excluded), so changing its request shape moves both generated files.

- [ ] Run the OpenAPI snapshot test. It **self-heals**: run 1 fails and rewrites the snapshot, run 2 passes with no code change. Commit the regenerated file.
- [ ] Run `npm run generate` in `integrations/n8n-nodes-diariz`. This one does **not** self-heal, and a stale `generated/index.ts` reds the non-required "n8n community node" check - it stayed broken across three merged PRs before. Commit the result.

## Task 16 - Release checklist and docs

- [ ] `version.json` -> `0.238.0`, plus all four mirrors: `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`. `versionMirrors.test.ts` fails the build if any drifts.
- [ ] `RELEASES[0]` entry in `apps/web/src/lib/releases.ts`: version, date, `pr`, headline, prose summary, `added` bullets. **Confirm the PR number** rather than assuming last + 1 - Dependabot PRs and issues share the sequence and nothing tests a wrong number.
- [ ] `CAPABILITIES` table row in the same file (one concise `| Feature | Description |` line, not prose).
- [ ] `AboutModal.tsx` disclaimers: add SkiaSharp - a new third-party library.
- [ ] README **Features** table row.
- [ ] `docs/features.md` prose bullet, in lockstep with the README row. Never one without the other.
- [ ] `docs/Overall_Synopsis_of_Platform.md`: SkiaSharp as a new API dependency, and images crossing the chat boundary.
- [ ] `docs/Data_Schema.md`: `ChatSession.ContextJson` gains a `screenshots` key. No DDL, no migration-history row.
- [ ] `apps/web/src/content/help/en/chat-over-transcripts.md`: the drag gesture and the vision-model requirement. ASCII only; keep the front-matter `summary` to two or three sentences.
- [ ] Add the deferred derivative cache to `untracked/Future_Roadmap_Hit_List.md` - it is gitignored, so it is invisible to any repo grep and will otherwise be lost.

## Task 17 - Verification

- [ ] `dotnet build Diariz.slnx` - clean.
- [ ] `dotnet test tests/Diariz.Api.Tests` - green, no warnings.
- [ ] `dotnet test tests/Diariz.Api.IntegrationTests` (needs Docker) - green.
- [ ] `cd apps/web && npm run build && npm test` - green, no warnings.
- [ ] **Live browser check.** jsdom cannot prove HTML5 drag and drop, and it computes no geometry, so the tray's layout is unverified by any test above. Rebuild the `api` and `web` containers (do not trust HMR or an incremental MSBuild - both serve stale code while looking fine), then in the running app: drag a real screenshot into the composer, confirm the thumbnail renders at a sane size and the composer does not overflow its panel, confirm the gate blocks with a non-vision model, and confirm a real answer comes back from a vision model. **Check `App__PublicUrl` before touching anything - the local `diariz` compose stack is production.**
- [ ] Confirm no em or en dashes entered user-facing strings (write the diff to a file, decode UTF-8 explicitly).
- [ ] Push and open a PR. Body states: feature, `0.238.0`, **server redeploy only**, no desktop release.

---

# Known risks

| Risk | Where it bites | Mitigation in this plan |
|---|---|---|
| Skia native load fails in the Linux container | Task 17 live check, not any test - dev is Windows | Live check is mandatory, not optional; `NoDependencies` variant chosen precisely to avoid fontconfig |
| Picker and resolver disagree about `images_supported` | Silent - user sees a model offered then refused | Task 3 extraction + Task 4 agreement test |
| The parts-array change leaks into non-vision calls | Every existing chat turn, tool round, summariser | Task 5 asserts byte-identical string form when no images |
| Gate test passes without a real gate | `fireEvent` fires on disabled controls | `userEvent` mandated in Task 13 |
| Stale `generated/index.ts` | A non-required CI check nobody looks at | Task 15 |
