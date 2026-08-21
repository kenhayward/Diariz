# Vision analysis for screenshots - design

Date: 2026-08-21
Status: approved
Ships as: one PR - `0.238.0`
Deployment surface: server redeploy only (web + API). No desktop shell files change.

## Problem

A recording can carry screen captures taken during the meeting. They are visible - the Notes tab shows a
strip of thumbnails, and the transcript weaves them in at the moment they were taken - but they are inert.
The chat panel sitting next to them can read every word of the transcript and cannot see a single pixel of
the slide that was on screen while those words were said.

So the one question a user most wants to ask about a capture ("what does this diagram show", "read me the
figures on that slide", "what error is this") is the one question the product cannot answer, even when the
administrator has a vision-capable model configured and routed.

Three things stand between here and there:

1. **Nothing consumes the capability flag.** `images_supported` already exists as a per-model parameter and
   already resolves through the four-layer stack into `LlmParameters.ImagesSupported`. Nothing reads it.
   It has been an inert declaration since it was added.
2. **The chat wire is text-only.** `ChatMessage` is `(Role, Content)` and the orchestrator shapes it as
   `{ role, content: "..." }`. The OpenAI vision shape needs `content` to become an array of parts.
3. **There is no gesture.** Thumbnails are buttons that open a viewer. Nothing connects the middle panel to
   the right one.

## Decisions taken

Recorded because each closed off an alternative that will look reasonable later.

1. **`images_supported` is the gate, not a new column.** The flag already exists, is already
   administrable in the model drawer as "Supports image input", and already resolves per call group. A
   second `SupportsVision` column beside it would be two sources for one fact.
2. **The client sends ids, never image bytes.** A turn carries `{ recordingId, screenshotId }` pairs. The
   API re-checks read access, loads the blob itself, and encodes it. The browser is never trusted to supply
   the pixels, the request body stays small, and a saved conversation stores references rather than
   megabytes of base64.
3. **Rescale to fit inside 1920x1080, never upscale.** The vision models in use degrade above that.
   `scale = min(1920/w, 1080/h)`, applied only when the capture exceeds the box.
4. **A capture already inside the box is passed through byte-for-byte.** `MeetingScreenshot` stores
   `Width`/`Height`, so this is decided by a DB read. The image library is never invoked and the model sees
   exactly what was captured.
5. **Resized output is JPEG q92; pass-through output stays PNG.** Resampling antialiases text, which
   destroys PNG's flat-colour compression - a downscaled 1920x1080 capture can encode 4-5x larger as PNG
   than as JPEG of the same pixels. Resampling has already given up pixel-exactness, so the lossy encode
   costs little more. Pass-through has given up nothing, so it stays lossless.
6. **SkiaSharp, not ImageSharp.** ImageSharp v3+ ships under the Six Labors Split License (free for OSS and
   small companies, commercial licence otherwise). SkiaSharp is MIT over Google's BSD Skia. It also needs no
   apt packages in the runtime image - `SkiaSharp.NativeAssets.Linux.NoDependencies` carries its own
   `libSkiaSharp.so` and we never render text, so fontconfig is not required. `src/Diariz.Api/Dockerfile`
   is untouched.
7. **Attached screenshots are sticky, like the file attachment pill.** They stay in the tray and are resent
   on every turn until the user removes them. That is what makes a follow-up question about the same image
   possible without re-dragging it, and it is what the "X removes it from the context" wording means.
8. **The Notes tab strip is the only drag source.** Not the transcript's inline rows: those are hand-rolled
   markup in `RecordingDetail.tsx` and would be a second implementation of the same gesture, with nowhere
   natural for the discoverability hint to sit.
9. **Send is blocked, not silently downgraded.** With shots attached and a model that cannot see them, the
   Send button is disabled and the composer says "Select a vision model". Sending the prompt without the
   images would answer a question about a picture the model never saw, which is worse than refusing.
10. **The picker marks which models can see.** Otherwise the warning names a remedy the user has no way to
    act on.
11. **No caching of the resized derivative.** Under decision 7 the same capture re-encodes on every
    follow-up turn. That is roughly 50-150ms of CPU against a multi-second model call, and caching means
    holding megabytes of image in API memory or adding a third blob per capture with its own quota
    accounting. Deferred deliberately, and recorded in `untracked/Future_Roadmap_Hit_List.md` so it is not
    lost.

## Accepted trade-off

**1920x1080 is a cap, not a target.** A 3840x2160 capture loses half its linear resolution. Small on-screen
text that was legible at 4K may not survive the downscale. This is taken deliberately to stay inside the
models' good range; it is not a bug when a dense screenshot reads poorly.

## Architecture

### The vision flag, exposed once

`ChatModelCatalog` becomes the source of `supportsImages` for the picker, the same way it is already the
single authority on which models chat may use. Its doc comment warns about exactly the failure this
invites: a picker that offers something the resolver then refuses, or the reverse.

To make that impossible, the parameter-layer stack for a model is extracted from `LlmSettingsResolver` into
a shared helper:

```
LlmParameterStack.For(model, group, defaults, platformSettings) -> List<string?>
  [ model's override row for `group`,
    model's ModelBase row,
    ...LlmPlatformLayers.Below(defaults, group, platformSettings) ]
```

`LlmSettingsResolver.ResolveAsync` and `ChatModelCatalog.ListAsync` both call it. The picker's flag is then
the same computation the pipeline will perform, not a second one that agrees by coincidence.

`ChatModelCatalog` gains `IOptions<LlmDefaultsOptions>` and reads `PlatformSettings` so it can build the
stack. `ChatModelOption` and `ChatModelDto` gain `SupportsImages`.

A platform with no `LlmModel` rows at all runs on the environment fallback, which has no row and therefore
no picker entry. `LlmDefaultsOptions.ImagesSupported` defaults to `false`, so that platform reports
not-capable and the gate holds.

### The wire

```
ChatStreamRequest    + IReadOnlyList<ChatScreenshotRefDto>? Screenshots
SavedChatContextDto  + IReadOnlyList<ChatScreenshotRefDto>? Screenshots
ChatScreenshotRefDto = (Guid RecordingId, Guid ScreenshotId)
```

`SavedChatContextDto` is serialised into `ChatSession.ContextJson`, which is already a JSON blob. **No
migration.** Older rows read the missing key as null, exactly as `modelId` did in 0.231.0. No
`MaintenanceController.CurrentFormat` bump: an additive key in an existing blob is forward-restore-safe.

### Request handling in `ChatController.Stream`

Order matters, because each step's failure means something different:

1. `_settings.ResolveAsync(...)` as today.
2. Existing recording-ownership check on `req.RecordingIds`, unchanged.
3. **If `req.Screenshots` is non-empty:**
   a. For each distinct `recordingId` in it, `IRoomScope.CanReadRecordingAsync` -> `404` on failure. Same
      response as an unreadable context recording: existence is not disclosed.
   b. Load the `MeetingScreenshot` rows constrained to *both* ids. A shot id that does not belong to the
      recording named is `404`, not a silent skip - it is a malformed request, and skipping would let a
      caller probe.
   c. `cfg.Parameters.ImagesSupported == false` -> `400` with a message naming the model. This is the
      backstop for the client gate, and the whole answer for API and MCP callers, which have no client.
   d. Encode each via `IVisionImageEncoder`.

Steps a-c run before any blob is read, so a rejected request costs no storage IO.

### `IVisionImageEncoder`

New service in `src/Diariz.Api/Services/`.

```
Task<VisionImage> EncodeAsync(MeetingScreenshot shot, CancellationToken ct)

VisionImage = (string DataUrl, int Width, int Height)   // the dimensions ACTUALLY sent
```

It returns the dimensions it actually produced, not just the data URL, because the context meter below
needs them and nothing else in the request knows them.

- `VisionImageBounds.Fit(width, height)` -> `(int W, int H)?`. Null means "already inside the box". This is
  a pure static function with no Skia and no storage, which is where the interesting cases live: landscape,
  portrait, exactly at bounds, one axis over, extreme aspect ratios, and never-upscale.
- Null -> `OpenReadAsync(shot.BlobKey)` -> `data:image/png;base64,...`, bytes untouched, dimensions as
  stored.
- Non-null -> decode with SkiaSharp, resample at high quality to the fitted size, encode JPEG q92 ->
  `data:image/jpeg;base64,...`.

Splitting the arithmetic out is what makes the bounds logic testable without Skia or MinIO; the encoder
itself gets a smaller test that synthesises a bitmap and asserts the output's dimensions and format.

### Message shape

`ChatMessage` gains `IReadOnlyList<string>? ImageDataUrls = null`. Defaulted, so every existing
construction site compiles and behaves identically.

`ChatContextBuilder.BuildMessages` attaches the data URLs to the **last user message** - the turn the images
are context for.

`ChatToolOrchestrator` (currently `seed.Select(m => (object)new { role = m.Role, content = m.Content })`)
shapes `content` as an OpenAI parts array **only when `ImageDataUrls` is non-empty**:

```json
{ "role": "user", "content": [
    { "type": "text", "text": "..." },
    { "type": "image_url", "image_url": { "url": "data:image/jpeg;base64,..." } } ] }
```

Otherwise it emits the string form exactly as today. Every non-vision caller, every tool round, and every
other consumer of the orchestrator are byte-identically unaffected.

### Context meter

`ChatController` currently computes `promptTokens` from `m.Content` only. An attached screenshot would
therefore be invisible to the dial - a large prompt showing as nearly empty. `ChatContextMeter` gains
`EstimateImageTokens(width, height) = ceil(width * height / 750)`, applied to the dimensions the encoder
actually produced. That is the common tile-based approximation and puts a full 1920x1080 capture at roughly
2,800 tokens. It is an estimate either way - the existing text figure is chars/4 - and the point is that the
gauge stops being confidently wrong about something that can be a fifth of a small context window.

### Web

| Component | Change |
|---|---|
| `ScreenshotStrip.tsx` | Thumbnails become `draggable`, writing `application/x-diariz-screenshot` JSON (`recordingId`, `screenshotId`, `capturedAtMs`) on `dragStart`. Behind an opt-in `draggable` prop. Click-to-open unaffected. |
| `ScreenshotsSection.tsx` | Passes `draggable`, and renders the hint line. |
| `ChatPanel.tsx` | `attachedShots` state; drop target around the composer with a drop-hover affordance; thumbnail tray above the textarea; vision gate on Send; refs on the wire and in save/load. |
| `ChatScreenshotTray.tsx` | **New.** The tray: thumbnails with a corner remove control. Extracted because `ChatPanel.tsx` is already 1142 lines. |
| `ChatModelPicker.tsx` | Marks vision-capable rows. |
| `lib/api.ts`, `lib/types.ts` | `screenshots` on the chat request and saved context; `supportsImages` on `ChatModelOption`. |

**Drop payload.** A custom MIME type rather than `text/plain`, so an arbitrary dragged word or URL cannot be
mistaken for a screenshot and a screenshot dragged elsewhere in the page does nothing surprising.

**Duplicates.** Dropping a shot already in the tray is a no-op, not a second copy.

**No cap on the number of attached screenshots.** At roughly 2,800 tokens each, a tray of ten is ~28k tokens
against the deployed vision model's 262,000-token window - a hard limit would refuse work the model can
comfortably do, to guard against a problem this platform does not have. The context dial is the feedback
mechanism instead: images are metered into it (see below), so a tray growing large is visible before it is a
problem rather than being silently refused at an arbitrary number.

**Empty states.** `ScreenshotsSection` already returns null when a recording has no captures, so the hint
line appears only where the gesture is possible. The tray likewise renders nothing when empty - the drop
target is the composer itself, which is always there.

**Attachments survive navigation.** The tray is not cleared when the user opens a different recording or
folder, matching the file attachment pill. A screenshot is a thing the user chose to discuss, not part of
the inferred context, and clearing it on navigation would destroy the common case of looking something up
mid-conversation.

**Thumbnails in the tray** use `api.screenshotThumbUrl(...)`, which already carries the bearer as
`access_token` for direct `<img>` loading. The full PNG is never fetched by the browser.

**The gate.** `blockedByVision = attachedShots.length > 0 && !selectedModel?.supportsImages`. When true,
Send is disabled and the composer shows "Select a vision model". No model selected at all (the environment
fallback) counts as not-capable, matching the server default.

**Restoring a saved conversation** drops any screenshot whose recording or shot has since been deleted,
silently. A saved chat should reopen, not error.

### Strings

New keys in all four locales (`en`, `de`, `es`, `fr`). Plain hyphens only - no em or en dashes.

| Namespace | Key | English |
|---|---|---|
| `workspace` | `screenshotsDragHint` | Drag and drop a screenshot to the chat prompt for image analysis |
| `chat` | `selectVisionModel` | Select a vision model |
| `chat` | `removeScreenshot` | Remove screenshot |
| `chat` | `dropScreenshot` | Drop to add to the prompt |
| `chat` | `modelSupportsImages` | Can read images |

## Testing

Every layer, red first. Mutation-verify each one.

**.NET unit (`tests/Diariz.Api.Tests`)**

- `VisionImageBounds.Fit`: within bounds -> null; over on width only; over on height only; over on both;
  exactly 1920x1080 -> null; extreme aspect ratio clamps on its long axis; never returns a size larger than
  the input.
- `ChatModelCatalog.ListAsync` reports `SupportsImages` from the model's Chat override, from its ModelBase
  row, and from the platform default when neither mentions it.
- The catalog and `LlmSettingsResolver` agree: same model, same group, same flag. This is the test that
  makes the shared stack load-bearing rather than incidental.
- `ChatController.Stream` -> `400` when screenshots are attached and the resolved model cannot see them.
- `Stream` -> `404` when a screenshot's recording is not readable by the caller.
- `Stream` -> `404` when a screenshot id does not belong to the recording it is paired with.
- `ChatToolOrchestrator` emits a parts array when `ImageDataUrls` is present, and the plain string form when
  it is not.
- `ChatContextMeter` counts attached images.

**.NET integration (`tests/Diariz.Api.IntegrationTests`, Docker)**

- Full round trip through real MinIO: a stored oversized PNG comes back as a `data:image/jpeg` URL whose
  decoded dimensions fit the box; a stored small PNG comes back as `data:image/png` with bytes identical to
  what was uploaded.
- A saved conversation's `ContextJson` carries the screenshot refs and reloads them.

**Web (vitest + RTL)**

- `dragStart` on a strip thumbnail writes the custom MIME payload.
- Dropping on the composer adds a thumbnail; dropping the same shot twice adds one.
- The remove control removes it.
- With shots attached and a non-vision model, Send is disabled and the warning renders. Use `userEvent`,
  not `fireEvent` - `fireEvent` fires handlers on disabled controls, so a lock test written with it passes
  for a reason the browser never reproduces.
- `api.chatStream` is called with the `screenshots` array.
- Switching to a vision-capable model clears the block.

## Release checklist

| # | Item | Change |
|---|---|---|
| 1 | `version.json` + 4 mirrors | `0.238.0` (feature: Minor +1, Build 0) |
| 2 | `releases.ts` `RELEASES[0]` | New entry, `pr` set before `gh pr create` exists to report it - confirm the number, do not assume last + 1 |
| 3 | `CAPABILITIES` row + `AboutModal.tsx` disclaimers | New capability row; SkiaSharp added to disclaimers (new third-party library) |
| 4 | README Features table | New row |
| 5 | `docs/features.md` | Matching prose bullet, in lockstep with 4 |
| 6 | `Overall_Synopsis_of_Platform.md` | New external dependency (SkiaSharp); images on the chat wire |
| 7 | `Data_Schema.md` | `ChatSession.ContextJson` gains a `screenshots` key. No DDL |

Also: `chat-over-transcripts.md` help article gains the gesture and the vision requirement - behaviour a
user relies on, not merely an inventory row.

## Explicitly out of scope

- Dragging from the transcript's inline screenshot rows (decision 8).
- Dropping an image file from the OS, or pasting one from the clipboard.
- Vision anywhere other than chat - summaries, minutes, formulas and actions are unchanged.
- Caching the resized derivative (decision 11).
- Making the 1920x1080 bound or the JPEG quality configurable.
