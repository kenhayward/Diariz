# Video Audio Extraction (drop an MP4, transcribe the audio)

**Date:** 2026-08-12
**Status:** Design, awaiting implementation plan

## 1. Goal

Let a user drop a **video** file - a recorded webinar, town hall, or all-hands - onto the recordings
panel and have Diariz transcribe it, **without ever storing the video**.

The audio is extracted in the browser, downmixed to mono, and uploaded as an ordinary `audio/webm`
recording. The video track never leaves the user's machine. From the moment the upload begins,
nothing downstream can tell the recording came from a video.

Target envelope: sources up to **4 hours** and **several gigabytes**.

## 2. Why not the obvious approaches

**Just accept `.mp4` on the server and let the worker's ffmpeg handle it.** This nearly works
already, which is the trap. `AudioFormats.Detect` maps any ISO-BMFF `ftyp` box to `"m4a"`
(`src/Diariz.Api/Services/AudioFormats.cs:26`), so an MP4 passes the sniff whenever
`Uploads:AllowAac` is on, and `whisperx.load_audio` (`src/Diariz.Worker/pipeline.py:207`) shells out
to ffmpeg, which ignores video without being asked. Transcription would work today. But the whole
video lands in MinIO and is billed against the user's quota - exactly the outcome to avoid.

**Extract server-side, in the API.** Fails on arithmetic before anything else. Four limits sit in
front of the upload endpoint (`tests/Diariz.Api.IntegrationTests/UploadSizeLimitIntegrationTests.cs:14`):
nginx 1 GiB, Kestrel 1 GiB, the multipart reader 1 GiB, then `Uploads:MaxBytes` at 500 MB. A 1-hour
1080p recording is typically 0.5-1.5 GB, and the 4-hour target case is 2-6 GB. The file cannot reach
a server-side extractor at all. Raising the ceilings would mean nginx buffering gigabytes to a temp
file per upload (`proxy_request_buffering` is on - see `docs/Research/zero-downtime-redeploy.md:75`),
long proxy timeouts, and a redeploy window that eats uploads. It would also require ffmpeg in the API
image, which today carries only `postgresql-client` and `curl`, putting a media parser handling
hostile input inside the auth and database process rather than in the isolated GPU worker.

**Upload the video, have the Python worker strip and replace the blob.** Reuses ffmpeg that is
already there, but stores the video transiently - minutes, or hours behind a queue backlog - which
contradicts the requirement outright. It also still cannot get a multi-GB file through the pipe, and
it charges the quota against the video size, so a 3 GB town hall is rejected for a 52 MB result.

**ffmpeg.wasm in the browser.** One dependency and one command, but a 25-30 MB wasm download and a
32-bit address space that cannot hold a multi-GB input. It fails precisely at the case this feature
exists for.

**WebAudio `decodeAudioData`.** No dependency at all, and genuinely the right answer for short clips.
But it is not streaming: it needs the entire source as one `ArrayBuffer` **plus** the decoded PCM.
Four hours at 16 kHz mono is ~920 MB of PCM on top of a multi-GB source buffer. It OOMs the tab at
the target size.

**Assemble it from permissive parts** - mp4box.js, a separate EBML demuxer, `webm-muxer`, and a
hand-written `AudioDecoder` -> downmix -> resample -> `AudioEncoder` pipeline. Three dependencies,
three licences, and roughly 300 lines of media plumbing to own and test. It is the fallback if
mediabunny disappoints; it is not worth paying for up front.

## 3. Non-goals

- **Server-side enforcement.** "No video is stored" is a guarantee of the web and desktop UI, not an
  invariant of the platform. A direct API, n8n, or MCP caller posting an MP4 still gets the video
  stored, exactly as today. Closing that needs an ISO-BMFF box-tree parser (and an EBML walk) in
  `AudioFormats`; it is deliberately deferred and recorded in section 8.
- **A new `RecordingSource.Video`.** What is stored genuinely *is* an audio upload. A new enum value
  would ripple into Postgres, the web display strings, four locale catalogs, the n8n node, and the
  OpenAPI snapshot, to record provenance nothing consumes.
- **`.avi`, `.wmv`, `.flv`.** Rejected with guidance to convert first.
- **A confirmation dialog before extraction.** Dropping a video behaves like dropping audio, with one
  extra phase.
- **Any change to the API, the Python worker, or the Electron shell.**

## 4. Architecture

### 4.1 Flow

```
drop / file picker
  -> classify (pure)                       audio | container | rejected
  -> [container] peek for a video track
       no video  -> pass through untouched (no re-encode)
       video     -> Worker: mediabunny conversion, discard video,
                            1 channel, Opus, 48 kHz, ~32 kbps
                    -> File("<name>.webm", type audio/webm)
  -> size pre-check against the RESULT
  -> api.uploadFile (unchanged)
  -> server stores an ordinary WebM upload
  -> Python worker transcribes (unchanged)
```

Everything is in `apps/web`. The extracted file is byte-for-byte the same *kind* of artifact the
browser recorder already produces - `Recorder.tsx:882` records with `mimeType: "audio/webm"`, which
is Opus in WebM - so playback, storage, quota, and the worker's decode path all need no change.
`webm` is already in `AudioFormats.AlwaysAllowed`, so the server is untouched.

### 4.2 The numbers that force the design

| | 1 hour | 4 hours |
| --- | --- | --- |
| Source MP4, 1080p | 0.5-1.5 GB | 2-6 GB |
| Decoded PCM, 16 kHz mono | ~230 MB | ~920 MB |
| WAV, 16 kHz mono 16-bit | ~115 MB | ~460 MB (92% of the cap) |
| **WebM/Opus, 48 kHz mono 32 kbps** | **~14 MB** | **~58 MB** |

WAV was considered and rejected on the right-hand column: at 4 hours it sits against
`Uploads:MaxBytes` with no headroom, uploads slowly, and gives narrowband playback. Opus at 48 kHz
costs one muxer, keeps playback full-band, and leaves 88% of the cap free.

Encoder settings are exact, not approximate: **Opus, 1 channel, 48 000 Hz, 32 000 bps.**

48 kHz is a passthrough rate for Zoom, Teams, OBS, Meet, and Webex output, so the common case needs
no resampling at all.

### 4.3 `lib/mediaKinds.ts` - pure classification

Answers one question about a `File`, using no browser media APIs:

- `audio` - `.wav .mp3 .flac .ogg .opus .m4a .aac`. Passes straight through, exactly as today.
- `container` - `.mp4 .m4v .mov .mkv .webm`. May or may not hold video; needs the peek in 4.6.
- `rejected` - everything else, with a message naming what to do instead.

Also owns the two size limits, which are now distinct (4.5).

### 4.4 `lib/uploadQueue.ts` - the state machine grows one phase

`runUploadBatch` is already a pure orchestrator taking injected `upload` and `precheck` dependencies
and reporting per-file status. It gains:

- a new `extracting` status carrying a `progress` fraction,
- an injected `extract` dependency of the same shape as the existing ones,
- a `cancelled` terminal status,
- and the reordered pre-check from 4.5.

It stays pure and fully unit-testable, which is where nearly all the real test value lives.
`UploadStatusList.tsx:10` keys a `Record<UploadItem["status"], string>` by status, so adding a status
raises a compile error at the render site rather than silently rendering nothing - keep that
exhaustiveness.

Per-file failure tolerance is preserved: one unextractable video does not abandon the rest of a batch.

### 4.5 The size-check ordering, which is the easiest thing to get wrong

`precheckUpload` currently rejects on extension **and** size in a single call, against
`MAX_UPLOAD_BYTES` (500 MB). Left alone, a 2.8 GB webinar is rejected before extraction ever runs -
that is, every real instance of the feature's motivating case.

It splits in two:

- a **source** guard of **8 GB**, applied to the dropped file. A sanity ceiling only: it is well above
  the 2-6 GB target envelope, and past it the browser will fail on memory regardless, so a clear
  up-front message beats a mid-extraction crash.
- the **result** guard, the existing 500 MB `Uploads:MaxBytes` mirror, applied to what extraction
  produced.

Audio files, having no extraction step, are checked exactly as they are today.

### 4.6 The video-track peek, and the cover-art trap

Branching on file extension is wrong: a `.webm` may be an audio-only recording or a screen capture,
and `.mkv` likewise. So any `container` file is opened with mediabunny and branched on whether a
**video track is present**.

Audio-only containers pass through completely untouched, with no re-encode - someone re-uploading
their own Diariz recording must not have it transcoded a second time. This also closes an existing
hole: today a `.webm` screen recording passes `AlwaysAllowed` and is stored with its video track.
Same code path, no extra work.

**The trap:** a media file can carry cover art as a still-image track, which a naive "has a video
track?" test reads as video. That would re-encode a file needing nothing done to it. `.m4a` and
`.aac` avoid this by staying on the `audio` path (4.3), unchanged from today - but a `.mp4` or `.mov`
holding only audio plus artwork does reach the peek, so the presence test must distinguish a real
video track from a single still image. It gets an explicit test with such a file.

### 4.7 `lib/videoAudio.ts` and `lib/videoAudio.worker.ts`

The single impure seam. Its whole public surface is:

```
extract(file, { onProgress, signal }) => Promise<File>
```

`videoAudio.ts` owns the Worker lifecycle and `AbortController` wiring and is the only file that
knows mediabunny exists. `videoAudio.worker.ts` runs the conversion: `video: { discard: true }`,
audio to `codec: "opus"`, `numberOfChannels: 1`, `sampleRate: 48000`, with `onProgress` driving the
UI.

**Why a Worker.** There is currently no `new Worker` anywhere in `apps/web` - this is new
infrastructure, and it is not optional. Mediabunny performs channel downmixing in JavaScript; four
hours of 48 kHz stereo is roughly 700 million samples of arithmetic. On the main thread that freezes
the UI for minutes. Vite bundles workers natively, so the cost is configuration, not a toolchain
change. `File` is structured-cloneable by reference, so handing the source across costs nothing.

**Termination, not reuse.** A cancelled or failed worker is terminated. Reusing one that is wedged
mid-conversion leaks a thread and its buffers for the remainder of the session.

### 4.8 Dependency

[mediabunny](https://github.com/Vanilagy/mediabunny) - pure TypeScript, zero transitive
dependencies, tree-shakeable, built on WebCodecs. Reads and writes `.mp4/.mov/.webm/.mkv`, so one
library replaces a demuxer per container family plus a muxer plus the decode/encode pipeline. Its
Conversion API streams and writes progressively rather than buffering, and exposes `onProgress`.

**Licence: MPL-2.0.** As an unmodified npm dependency inside the bundle this is fine - the file-level
copyleft reaches only mediabunny's own files. It requires an attribution entry, which goes in the
`AboutModal.tsx` disclaimers list already maintained for third-party libraries.

**Maturity risk** is real - it is a young library. The mitigation is the narrow interface in 4.7,
which the design needs for testability anyway: replacing it touches one file.

## 5. Error handling

The governing rule: **the video is never uploaded as a fallback.** Every failure ends with the item
marked failed and the source discarded locally.

| Case | User-facing outcome |
| --- | --- |
| Browser lacks WebCodecs / `AudioDecoder` | "This browser can't extract audio from video. Try Chrome or Edge, or the desktop app." |
| Video has no audio track | "That video has no audio track." |
| Audio codec cannot be decoded | "Couldn't decode this video's audio." |
| `.avi` / `.wmv` / `.flv` | Rejected at classification, with guidance to convert to MP4 first. |
| Extraction throws, or runs out of memory | Generic failure, suggesting exporting the audio separately. |
| User cancels | Marked cancelled. No upload, no partial file. |
| Extracted audio still exceeds 500 MB | The existing size message, now measured against the result. |

All strings are added to **all four** locale catalogs (`de`, `en`, `es`, `fr`) - `locales.test.ts`
enforces key parity - using plain hyphens, never em- or en-dashes.

## 6. Testing

TDD: red first, then the minimal code to pass.

**Unit tests (vitest), covering the pure units:**

- classification of every accepted and rejected extension, and both `.webm` shapes;
- the reordered pre-check - specifically that a 2.8 GB *source* survives a gate that a 2.8 GB
  *upload* would fail;
- the queue state machine driven by an injected fake extractor: progress propagation, cancel
  mid-extract, extraction failure not abandoning the rest of the batch, and audio files skipping
  extraction entirely.

**Not testable in jsdom:** WebCodecs and mediabunny do not exist there. This is precisely why
`videoAudio.ts` is thin - the untestable surface is kept small and everything meaningful sits on the
pure side of the injected interface.

**Mutation verification is mandatory.** Every new test is confirmed to fail when the production line
it covers is broken, then restored. Tests that cannot fail are this repository's most common defect
class, and an injected-fake test is prime habitat for one.

**Browser verification is mandatory** - jsdom proves nothing about whether extraction works. Against
the dev server, with the recordings panel displayed:

1. a genuine ~1 GB MP4, dropped, extracted, uploaded, and transcribed end to end;
2. an `.mkv`;
3. an audio-only `.webm`, confirmed to pass through **without** re-encoding;
4. an audio-only `.mp4` carrying cover art, confirmed not to be misread as video;
5. cancel mid-extraction, confirming no upload occurs and the worker is terminated.

## 7. Release surface

Functional enhancement, so **Minor +1: 0.208.0 -> 0.209.0**.

1. `version.json` plus all four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
   `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
   `versionMirrors.test.ts` fails the build on any drift.
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`, with the PR number.
3. A `CAPABILITIES` table row - the app's scope genuinely changes - and an `AboutModal.tsx`
   disclaimer for mediabunny (MPL-2.0).
4. The README **Features** table row.
5. The matching `docs/features.md` prose bullet, in lockstep with (4).
6. `docs/Overall_Synopsis_of_Platform.md`: a client-side pre-processing stage ahead of upload is a
   real change to how ingest works, plus a new external dependency.
7. `docs/Data_Schema.md`: **no change**. No schema, storage-layout, or migration change.

Also: `apps/web/src/content/help/en/uploading-audio.md`, because what a user may drop is exactly the
behaviour that article describes.

**Deployment surface: server redeploy only, no desktop release.** Nothing under `apps/desktop/src`,
`apps/desktop/build`, or `electron-builder.config.js` is touched; installed desktop apps load the web
app from the server origin and pick this up automatically.

## 8. Open risks and deferred work

- **The server-side hole stays open.** A direct API, n8n, or MCP caller can still post an MP4 and
  have the video stored. Unchanged from today, but now a documented gap rather than an unnoticed one.
  Closing it means parsing the ISO-BMFF box tree for a video handler, plus an EBML walk for
  Matroska - its own piece of work.
- **Mediabunny maturity.** Contained by the interface in 4.7; the fallback is the assemble-it-yourself
  path in section 2.
- **Opus encoding availability.** WebCodecs Opus encoding is present in Chromium and Safari, which
  covers the web app and, since the shell is Chromium, the desktop app. A browser without it fails
  cleanly per section 5 rather than silently producing something unexpected.
- **Non-48 kHz sources** (44.1 kHz from some editors) rely on mediabunny's internal resampling. Worth
  one browser check, but off the common path.
