# Video Audio Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user drop a video file (MP4/MOV/MKV/WebM) onto the recordings list and have Diariz
transcribe its audio, extracting mono Opus audio in the browser so the video never reaches the server.

**Architecture:** All changes are in `apps/web`. A new pure module classifies dropped files; the
existing pure upload queue gains an `extracting` phase with progress and cancel; a thin seam owns a
Web Worker that runs a mediabunny conversion (discard video, 1 channel, Opus 48 kHz). The extracted
file is an ordinary `audio/webm` upload - the same artifact the browser recorder already produces -
so the API, the Python worker, and the Electron shell are untouched.

**Tech Stack:** React 19, TypeScript, Vite 8, vitest 4 + @testing-library/react, i18next, and
`mediabunny` (new dependency, MPL-2.0, WebCodecs-based).

**Spec:** `docs/superpowers/specs/2026-08-12-video-audio-extraction-design.md`

## Global Constraints

- **Encoder settings are exact:** Opus, `numberOfChannels: 1`, `sampleRate: 48000`, bitrate `32e3`.
- **Output container:** WebM. The uploaded `File` has type `audio/webm` and a `.webm` name.
- **The video is never uploaded as a fallback.** Every extraction failure marks the item failed and
  discards the source locally.
- **Source size ceiling:** 8 GiB. **Result size ceiling:** the existing 500 MB `MAX_UPLOAD_BYTES`.
- **No em-dashes or en-dashes** in any user-facing string, locale catalog, release note, README,
  `docs/features.md`, or help article. Plain hyphen `-` only. (Code comments are unaffected.)
- **New `t()` keys go in all four locale catalogs** - `de`, `en`, `es`, `fr` - or
  `apps/web/src/locales.test.ts` fails the build on key parity and on empty values.
- **Failure message strings returned from pure helpers stay plain English**, matching the existing
  `precheckUpload` convention. Only `t()`-rendered status labels are localized.
- **Version bump is Minor:** `0.208.0` -> `0.209.0`, in `version.json` plus all four mirrors.
- **Never `git add -A`** in this repository - stage explicit paths only.
- **Every new test must be mutation-verified:** break the line it covers, watch it fail, restore.
- Run web tests from `apps/web` with `npm test`; a single file with `npm test -- src/lib/x.test.ts`.

---

### Task 1: `mediaKinds.ts` - pure classification and the split size guards

This is the module that decides what a dropped file *is*, and it is the piece that fixes the ordering
bug: today `precheckUpload` rejects on extension **and** size together, so a 2.8 GB webinar would be
rejected by the 500 MB cap before extraction ever runs. Type and a generous ceiling are judged on the
**source**; the 500 MB cap is judged on the **result**.

Note `.webm` moves from the audio list to the container list. That is deliberate: a `.webm` may be an
audio-only recording or a screen capture, and only a peek inside can tell.

**Files:**
- Create: `apps/web/src/lib/mediaKinds.ts`
- Create: `apps/web/src/lib/mediaKinds.test.ts`
- Modify: `apps/web/src/lib/audioFormats.ts` (remove the four exports that move here)
- Modify: `apps/web/src/lib/audioFormats.test.ts` (drop the tests for the removed exports)

**Interfaces:**
- Consumes: `fileExtension` from `./audioFormats`, `MAX_UPLOAD_BYTES` from `./audioFormats`
- Produces:
  - `type MediaKind = "audio" | "container" | "rejected"`
  - `classifyFile(file: { name: string }): MediaKind`
  - `sourceProblem(file: { name: string; size: number }, maxBytes?: number): string | null`
  - `resultProblem(file: { size: number }, maxBytes?: number): string | null`
  - `MEDIA_ACCEPT_ATTR: string`
  - `MAX_SOURCE_BYTES: number`

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/mediaKinds.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import {
  classifyFile,
  sourceProblem,
  resultProblem,
  MEDIA_ACCEPT_ATTR,
  MAX_SOURCE_BYTES,
} from "./mediaKinds";

describe("mediaKinds", () => {
  it("classifies audio formats as pass-through", () => {
    for (const name of ["a.wav", "a.mp3", "a.flac", "a.ogg", "a.opus", "a.m4a", "a.aac"])
      expect(classifyFile({ name }), name).toBe("audio");
  });

  it("classifies containers that may hold video, including .webm", () => {
    for (const name of ["a.mp4", "a.m4v", "a.mov", "a.mkv", "a.webm"])
      expect(classifyFile({ name }), name).toBe("container");
  });

  it("rejects formats we cannot demux", () => {
    for (const name of ["a.avi", "a.wmv", "a.flv", "a.txt", "noext"])
      expect(classifyFile({ name }), name).toBe("rejected");
  });

  it("is case-insensitive", () => {
    expect(classifyFile({ name: "Town Hall.MP4" })).toBe("container");
    expect(classifyFile({ name: "Memo.WAV" })).toBe("audio");
  });

  // The regression this whole task exists for: a multi-GB video must survive the source gate that a
  // multi-GB *upload* would fail, because extraction has not turned it into ~52 MB yet.
  it("lets a 2.8 GB video past the source guard", () => {
    expect(sourceProblem({ name: "Q3 Town Hall.mp4", size: 2_800_000_000 })).toBeNull();
  });

  it("flags an unsupported type, an empty file, and an absurd source", () => {
    expect(sourceProblem({ name: "clip.avi", size: 100 })).toMatch(/convert/i);
    expect(sourceProblem({ name: "empty.mp4", size: 0 })).toMatch(/empty/i);
    expect(sourceProblem({ name: "huge.mp4", size: MAX_SOURCE_BYTES + 1 })).toMatch(/too large/i);
  });

  it("applies the upload cap to the result only", () => {
    expect(resultProblem({ size: 52_000_000 })).toBeNull();
    expect(resultProblem({ size: 600_000_000 })).toMatch(/too large/i);
    expect(resultProblem({ size: 50 }, 10)).toMatch(/too large/i);
  });

  it("offers both audio and video in the file picker", () => {
    expect(MEDIA_ACCEPT_ATTR).toContain(".wav");
    expect(MEDIA_ACCEPT_ATTR).toContain(".mp4");
    expect(MEDIA_ACCEPT_ATTR).toContain(".mkv");
    expect(MEDIA_ACCEPT_ATTR).toContain("audio/*");
    expect(MEDIA_ACCEPT_ATTR).toContain("video/*");
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd apps/web && npm test -- src/lib/mediaKinds.test.ts`
Expected: FAIL - `Failed to resolve import "./mediaKinds"`.

- [ ] **Step 3: Write the implementation**

Create `apps/web/src/lib/mediaKinds.ts`:

```ts
import { fileExtension, MAX_UPLOAD_BYTES } from "./audioFormats";

// What a dropped file is, before anything is decoded. This is UX and ordering only - the server
// independently sniffs the actual bytes of whatever finally gets uploaded.
//
// The split matters: type and a sanity ceiling are judged on the SOURCE (which may be a multi-GB
// video), while the 500 MB upload cap is judged on the RESULT (the extracted audio). Judging both on
// the source rejected every real webinar before extraction could shrink it.

/// `audio` uploads unchanged; `container` may hold a video track and needs a peek; `rejected` cannot
/// be demuxed in the browser at all.
export type MediaKind = "audio" | "container" | "rejected";

/// Formats that are audio by definition, uploaded byte-for-byte as today. `.m4a`/`.aac` stay here
/// deliberately: they are ISO-BMFF and could in principle carry a track, but treating them as
/// containers would mean re-encoding ordinary audio uploads over embedded cover art.
const AUDIO_EXTENSIONS = ["wav", "mp3", "flac", "ogg", "opus", "m4a", "aac"] as const;

/// Formats that may hold video. `.webm` is here rather than in the audio list because it is genuinely
/// ambiguous - the browser recorder writes audio-only WebM, but a screen capture is also WebM.
const CONTAINER_EXTENSIONS = ["mp4", "m4v", "mov", "mkv", "webm"] as const;

/// The `accept` attribute for the hidden <input type="file">.
export const MEDIA_ACCEPT_ATTR =
  [...AUDIO_EXTENSIONS, ...CONTAINER_EXTENSIONS].map((e) => `.${e}`).join(",") + ",audio/*,video/*";

/// Sanity ceiling on the dropped file. Well above the 2-6 GB target envelope; past it the browser
/// runs out of memory regardless, and a clear message up front beats a crash midway through.
export const MAX_SOURCE_BYTES = 8 * 1024 * 1024 * 1024;

export function classifyFile(file: { name: string }): MediaKind {
  const ext = fileExtension(file.name);
  if ((AUDIO_EXTENSIONS as readonly string[]).includes(ext)) return "audio";
  if ((CONTAINER_EXTENSIONS as readonly string[]).includes(ext)) return "container";
  return "rejected";
}

/// Judged on the dropped file, before extraction. Returns a message, or null to proceed.
export function sourceProblem(
  file: { name: string; size: number },
  maxBytes = MAX_SOURCE_BYTES,
): string | null {
  if (classifyFile(file) === "rejected")
    return "Unsupported file type. Use WAV, MP3, FLAC, Ogg/Opus, WebM, M4A, or a video (MP4, MOV, MKV). Convert anything else first.";
  if (file.size === 0) return "That file is empty.";
  if (maxBytes > 0 && file.size > maxBytes)
    return `File too large. The maximum is ${Math.round(maxBytes / (1024 * 1024 * 1024))} GB.`;
  return null;
}

/// Judged on what will actually be uploaded - the extracted audio for a video, or the original file
/// for an audio upload. Type is already settled by `sourceProblem`, so this is size only.
export function resultProblem(file: { size: number }, maxBytes = MAX_UPLOAD_BYTES): string | null {
  if (maxBytes > 0 && file.size > maxBytes)
    return `File too large. The maximum is ${Math.round(maxBytes / (1024 * 1024))} MB.`;
  return null;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd apps/web && npm test -- src/lib/mediaKinds.test.ts`
Expected: PASS, 7 tests.

- [ ] **Step 5: Mutation-verify the ordering test**

In `mediaKinds.ts`, temporarily change `sourceProblem`'s default from `MAX_SOURCE_BYTES` to
`MAX_UPLOAD_BYTES`. Run the tests again.
Expected: FAIL on "lets a 2.8 GB video past the source guard". **Restore the line.**

This is the one test that must genuinely fail, because it encodes the bug the task exists to prevent.

- [ ] **Step 6: Remove the four exports that moved**

In `apps/web/src/lib/audioFormats.ts`, delete `ACCEPTED_AUDIO_EXTENSIONS`, `AUDIO_ACCEPT_ATTR`,
`isAcceptedAudioFilename`, and `precheckUpload`. Keep `MAX_UPLOAD_BYTES`, `fileExtension`, and
`titleFromFilename`. Update the file's header comment to:

```ts
// Shared filename/size helpers for uploads. Classification of *what a dropped file is* lives in
// mediaKinds.ts; this is UX only - the server independently validates the actual bytes it receives.
```

In `apps/web/src/lib/audioFormats.test.ts`, delete the `"accepts known audio extensions, rejects others"`,
`"precheck flags type, emptiness, and size"`, and `"exposes an accept attribute covering the formats"`
tests, and remove `isAcceptedAudioFilename`, `precheckUpload`, and `AUDIO_ACCEPT_ATTR` from the import.
The two remaining tests (`fileExtension`, `titleFromFilename`) stay as they are.

- [ ] **Step 7: Run the whole web suite**

Run: `cd apps/web && npm test`
Expected: FAIL - `uploadQueue.ts` and `Recorder.tsx` still import the deleted exports. That is
expected and is fixed in Tasks 2 and 4. Confirm the **only** failures name `precheckUpload` or
`AUDIO_ACCEPT_ATTR`; anything else is a real regression to fix now.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/mediaKinds.ts apps/web/src/lib/mediaKinds.test.ts apps/web/src/lib/audioFormats.ts apps/web/src/lib/audioFormats.test.ts
git commit -m "feat(web): classify dropped media and split source/result size guards"
```

---

### Task 2: `uploadQueue.ts` - an extraction phase with progress and cancel

The queue is already a pure orchestrator with injected dependencies, which is exactly the shape this
needs. It gains an `extracting` status carrying a 0..1 fraction, a `cancelled` terminal status, an
injected `extract`, and a handle the UI uses to cancel one in-flight item.

Extraction is only attempted for `container` files, and `extract` returns the **original File**
unchanged when the container holds no video - so re-uploading your own WebM recording never triggers
a re-encode.

**Files:**
- Modify: `apps/web/src/lib/uploadQueue.ts` (full rewrite, below)
- Modify: `apps/web/src/lib/uploadQueue.test.ts` (add four tests)

**Interfaces:**
- Consumes: `classifyFile`, `sourceProblem`, `resultProblem`, `MediaKind` from `./mediaKinds`
- Produces:
  - `type UploadItemStatus = "queued" | "extracting" | "uploading" | "done" | "failed" | "cancelled"`
  - `interface UploadItem { id, name, status, error?, progress? }`
  - `interface ExtractOptions { onProgress: (fraction: number) => void; signal: AbortSignal }`
  - `interface UploadBatchHandle { cancel: (id: string) => void }`
  - `RunUploadBatchDeps` gains `precheck?`, `sourceCheck?`, `classify?`, `extract?`, `onHandle?`
  - `runUploadBatch(files, deps): Promise<UploadItem[]>` (unchanged signature)

- [ ] **Step 1: Write the failing tests**

Append to `apps/web/src/lib/uploadQueue.test.ts` (inside the existing `describe`):

```ts
  it("extracts containers, passes audio straight through, and reports progress", async () => {
    const video = file("Town Hall.mp4", 2_800_000_000);
    const audio = file("memo.wav");
    const extracted = file("Town Hall.webm", 52_000_000);
    const extract = vi.fn(async (_f: File, o: { onProgress: (n: number) => void }) => {
      o.onProgress(0.5);
      return extracted;
    });
    const upload = vi.fn(async () => {});
    const snapshots: UploadItem[][] = [];

    const items = await runUploadBatch([video, audio], {
      upload,
      extract,
      onUpdate: (i) => snapshots.push(i.map((x) => ({ ...x }))),
    });

    expect(items.map((i) => i.status)).toEqual(["done", "done"]);
    expect(extract).toHaveBeenCalledTimes(1); // the .wav never reaches the extractor
    expect(upload.mock.calls[0][0]).toBe(extracted); // the video uploaded its EXTRACTED file
    expect(upload.mock.calls[1][0]).toBe(audio); // the .wav uploaded itself, untouched
    expect(snapshots.some((s) => s[0].status === "extracting")).toBe(true);
    expect(snapshots.some((s) => s[0].progress === 0.5)).toBe(true);
  });

  it("uploads a passed-through container without re-encoding it", async () => {
    const source = file("screen.webm", 1_000);
    // A container with no video track: the extractor hands the original File straight back.
    const extract = vi.fn(async (f: File) => f);
    const upload = vi.fn(async () => {});

    await runUploadBatch([source], { upload, extract, onUpdate: () => {} });

    expect(upload).toHaveBeenCalledWith(source);
  });

  it("marks a cancelled extraction cancelled and keeps going", async () => {
    let handle: UploadBatchHandle | undefined;
    const extract = vi.fn(
      (_f: File, o: { signal: AbortSignal }) =>
        new Promise<File>((_resolve, reject) => {
          o.signal.addEventListener("abort", () => reject(new Error("aborted")));
        }),
    );
    const upload = vi.fn(async () => {});

    const promise = runUploadBatch([file("a.mp4"), file("b.wav")], {
      upload,
      extract,
      onUpdate: () => {},
      onHandle: (h) => {
        handle = h;
      },
    });
    handle!.cancel("0-a.mp4");
    const items = await promise;

    expect(items.map((i) => i.status)).toEqual(["cancelled", "done"]);
    expect(upload).toHaveBeenCalledTimes(1); // the cancelled video never uploaded
  });

  it("fails an unextractable video without uploading it", async () => {
    const extract = vi.fn(async () => {
      throw new Error("That video has no audio track.");
    });
    const upload = vi.fn(async () => {});

    const items = await runUploadBatch([file("silent.mp4"), file("ok.mp3")], {
      upload,
      extract,
      onUpdate: () => {},
    });

    expect(items[0].status).toBe("failed");
    expect(items[0].error).toBe("That video has no audio track.");
    expect(upload).toHaveBeenCalledTimes(1); // never falls back to uploading the video
  });
```

Update the imports at the top of the file to:

```ts
import { runUploadBatch, type UploadItem, type UploadBatchHandle } from "./uploadQueue";
```

Also update the existing first test: its `["good.mp3", "notes.txt", "boom.wav"]` expectations are
unchanged, but `.txt` is now rejected by `sourceProblem`, so change
`expect(items[1].error).toMatch(/unsupported/i)` to `expect(items[1].error).toMatch(/Unsupported file type/)`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd apps/web && npm test -- src/lib/uploadQueue.test.ts`
Expected: FAIL - `UploadBatchHandle` is not exported, and `extract`/`onHandle` are not in the deps type.

- [ ] **Step 3: Write the implementation**

Replace the whole of `apps/web/src/lib/uploadQueue.ts`:

```ts
import { classifyFile, resultProblem, sourceProblem, type MediaKind } from "./mediaKinds";

// Pure orchestration for uploading a batch of files one at a time, reporting per-file status as it
// goes. Tolerant of partial failures: a rejected file (bad type / too big / unextractable / server
// error) is marked failed and the rest of the batch still runs. The React layer supplies `upload` and
// `extract` (which should throw a clean Error) and renders the status list from `onUpdate`.
//
// Three stages, in this order, and the order is the point:
//   1. source  - type and a sanity ceiling, judged on the dropped file (which may be a multi-GB video)
//   2. extract - containers only; strips video, or hands the file straight back if it holds none
//   3. result  - the 500 MB upload cap, judged on what is actually going to be sent
// Judging the upload cap on the source rejected every real webinar before extraction could shrink it.

export type UploadItemStatus = "queued" | "extracting" | "uploading" | "done" | "failed" | "cancelled";

export interface UploadItem {
  id: string;
  name: string;
  status: UploadItemStatus;
  error?: string;
  /// 0..1 while `extracting`; undefined in every other status.
  progress?: number;
}

export interface ExtractOptions {
  onProgress: (fraction: number) => void;
  signal: AbortSignal;
}

/// Lets the UI cancel one in-flight item. Handed to the caller once, via `onHandle`.
export interface UploadBatchHandle {
  cancel: (id: string) => void;
}

export interface RunUploadBatchDeps {
  upload: (file: File) => Promise<void>;
  onUpdate: (items: UploadItem[]) => void;
  /// Called after each successful upload (e.g. refresh the recordings list).
  onSuccess?: () => void;
  /// Stage 1. Defaults to `sourceProblem`.
  sourceCheck?: (file: File) => string | null;
  /// Stage 3. Defaults to `resultProblem`.
  precheck?: (file: File) => string | null;
  classify?: (file: File) => MediaKind;
  /// Stage 2. Strips video from a container, returning the ORIGINAL File when it holds none - so an
  /// audio-only WebM is never needlessly re-encoded. Omitted entirely, containers upload as-is.
  extract?: (file: File, opts: ExtractOptions) => Promise<File>;
  /// Receives the cancel handle once, before any work starts.
  onHandle?: (handle: UploadBatchHandle) => void;
}

export async function runUploadBatch(files: File[], deps: RunUploadBatchDeps): Promise<UploadItem[]> {
  const sourceCheck = deps.sourceCheck ?? sourceProblem;
  const precheck = deps.precheck ?? resultProblem;
  const classify = deps.classify ?? classifyFile;
  const items: UploadItem[] = files.map((f, i) => ({ id: `${i}-${f.name}`, name: f.name, status: "queued" }));
  const emit = () => deps.onUpdate(items.map((i) => ({ ...i })));
  const controllers = new Map<string, AbortController>();
  deps.onHandle?.({ cancel: (id) => controllers.get(id)?.abort() });
  emit();

  for (let i = 0; i < files.length; i++) {
    const set = (patch: Partial<UploadItem>) => {
      items[i] = { ...items[i], ...patch };
      emit();
    };

    const problem = sourceCheck(files[i]);
    if (problem) {
      set({ status: "failed", error: problem });
      continue;
    }

    let file = files[i];
    if (classify(files[i]) === "container" && deps.extract) {
      const controller = new AbortController();
      controllers.set(items[i].id, controller);
      set({ status: "extracting", progress: 0 });
      try {
        file = await deps.extract(files[i], {
          onProgress: (p) => set({ progress: p }),
          signal: controller.signal,
        });
      } catch (e) {
        // An abort is the user's choice, not a failure - and either way the video is never uploaded.
        set(
          controller.signal.aborted
            ? { status: "cancelled", progress: undefined }
            : {
                status: "failed",
                progress: undefined,
                error: e instanceof Error ? e.message : "Couldn't extract audio from that video.",
              },
        );
        continue;
      } finally {
        controllers.delete(items[i].id);
      }
    }

    const resultIssue = precheck(file);
    if (resultIssue) {
      set({ status: "failed", progress: undefined, error: resultIssue });
      continue;
    }

    set({ status: "uploading", progress: undefined });
    try {
      await deps.upload(file);
      set({ status: "done" });
      deps.onSuccess?.();
    } catch (e) {
      set({ status: "failed", error: e instanceof Error ? e.message : "Upload failed." });
    }
  }

  return items;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npm test -- src/lib/uploadQueue.test.ts`
Expected: PASS, 7 tests.

- [ ] **Step 5: Mutation-verify the two tests that matter most**

a) In the `catch` block, replace the whole ternary with
`set({ status: "failed", progress: undefined, error: "x" })`. Run the tests.
Expected: FAIL on "marks a cancelled extraction cancelled and keeps going". **Restore.**

b) In the `catch` block, delete the `continue;` that follows `set(...)`. Run the tests.
Expected: FAIL on "fails an unextractable video without uploading it" - `upload` is called twice.
**Restore.** This is the guard that stops a failed extraction from uploading the video.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/lib/uploadQueue.ts apps/web/src/lib/uploadQueue.test.ts
git commit -m "feat(web): add an extraction phase with progress and cancel to the upload queue"
```

---

### Task 3: `videoAudio.ts` - the mediabunny seam and its Web Worker

The only impure unit. Its entire public surface is one function matching the `extract` dependency
Task 2 defined, so nothing above it knows mediabunny exists.

**Why a Worker:** mediabunny does channel downmixing in JavaScript, and four hours of 48 kHz stereo is
roughly 700 million samples of arithmetic. On the main thread that freezes the UI for minutes. There
is currently no `new Worker` anywhere in `apps/web`; Vite bundles workers natively from a
`new URL(..., import.meta.url)` reference, so no build config change is needed.

**The Worker must be created lazily inside `extractAudio`, never at module scope.** jsdom has no
`Worker`, and `uploadContext.tsx` imports this module - a module-scope `new Worker(...)` would break
`uploadContext.test.tsx` on import.

**Files:**
- Create: `apps/web/src/lib/videoAudio.ts`
- Create: `apps/web/src/lib/videoAudio.worker.ts`
- Modify: `apps/web/package.json` (add `mediabunny` to `dependencies`)

**Interfaces:**
- Consumes: `ExtractOptions` from `./uploadQueue`
- Produces: `extractAudio(file: File, opts: ExtractOptions): Promise<File>` - matching
  `RunUploadBatchDeps["extract"]` exactly

- [ ] **Step 1: Install the dependency**

```bash
cd apps/web && npm install mediabunny
```

Confirm the installed licence is MPL-2.0 before continuing:

```bash
cd apps/web && node -e "console.log(require('./node_modules/mediabunny/package.json').license)"
```

Expected: `MPL-2.0`. If it is anything else, stop and raise it - the spec's licensing decision was
made on this basis.

- [ ] **Step 2: Write the worker**

Create `apps/web/src/lib/videoAudio.worker.ts`:

```ts
/// Runs the mediabunny conversion off the main thread. Owns no policy: it peeks for a video track,
/// converts when there is one, and reports back. All decisions about what to do with the result live
/// in videoAudio.ts and the upload queue.
import {
  ALL_FORMATS,
  BlobSource,
  BufferTarget,
  Conversion,
  Input,
  Output,
  WebMOutputFormat,
} from "mediabunny";

export type WorkerRequest = { type: "start"; file: File } | { type: "cancel" };

export type WorkerResponse =
  | { type: "progress"; fraction: number }
  /// The container holds no video: the caller should upload the original file untouched.
  | { type: "passthrough" }
  | { type: "done"; buffer: ArrayBuffer }
  | { type: "error"; message: string };

const post = (m: WorkerResponse, transfer?: Transferable[]) =>
  (self as unknown as Worker).postMessage(m, transfer ?? []);

let conversion: Conversion | null = null;

self.onmessage = async (e: MessageEvent<WorkerRequest>) => {
  if (e.data.type === "cancel") {
    await conversion?.cancel();
    return;
  }

  try {
    const input = new Input({ formats: ALL_FORMATS, source: new BlobSource(e.data.file) });

    if (!(await input.getPrimaryAudioTrack())) {
      post({ type: "error", message: "That video has no audio track." });
      return;
    }

    // Cover art shows up as a single-frame video track; a real video runs for the length of the file.
    // Without this, an audio file with embedded artwork would be needlessly re-encoded.
    const videoTrack = await input.getPrimaryVideoTrack();
    const videoSeconds = videoTrack ? await videoTrack.computeDuration() : 0;
    if (videoSeconds <= 1) {
      post({ type: "passthrough" });
      return;
    }

    const output = new Output({ format: new WebMOutputFormat(), target: new BufferTarget() });
    conversion = await Conversion.init({
      input,
      output,
      video: { discard: true },
      audio: { codec: "opus", numberOfChannels: 1, sampleRate: 48000, bitrate: 32e3 },
    });
    if (!conversion.isValid) {
      post({ type: "error", message: "Couldn't decode this video's audio." });
      return;
    }
    conversion.onProgress = (fraction: number) => post({ type: "progress", fraction });

    await conversion.execute();
    const buffer = output.target.buffer;
    if (!buffer) {
      post({ type: "error", message: "Couldn't extract audio from that video." });
      return;
    }
    post({ type: "done", buffer }, [buffer]);
  } catch (err) {
    post({ type: "error", message: err instanceof Error ? err.message : "Couldn't extract audio from that video." });
  } finally {
    conversion = null;
  }
};
```

If the installed mediabunny rejects the bare `bitrate: 32e3` on the audio options, use its quality
wrapper instead - `import { Quality } from "mediabunny"` and `quality: new Quality({ bitrate: 32e3 })`
- keeping every other value identical. Task 5's browser check is what confirms which form this build
accepts.

- [ ] **Step 3: Write the main-thread seam**

Create `apps/web/src/lib/videoAudio.ts`:

```ts
import type { ExtractOptions } from "./uploadQueue";
import type { WorkerResponse } from "./videoAudio.worker";

/// Extract mono Opus audio from a container, off the main thread.
///
/// Returns the ORIGINAL File when the container holds no video - an audio-only WebM (say, one of our
/// own recordings being re-uploaded) must not be re-encoded. Throws a plain Error whose message is
/// shown to the user; the caller never falls back to uploading the video.
///
/// The Worker is constructed per call, not at module scope: jsdom has no `Worker`, and this module is
/// imported by uploadContext.tsx, which is component-tested.
export function extractAudio(file: File, opts: ExtractOptions): Promise<File> {
  return new Promise<File>((resolve, reject) => {
    if (typeof Worker === "undefined" || typeof AudioDecoder === "undefined") {
      reject(new Error("This browser can't extract audio from video. Try Chrome or Edge, or the desktop app."));
      return;
    }

    const worker = new Worker(new URL("./videoAudio.worker.ts", import.meta.url), { type: "module" });
    // A cancelled or failed conversion is terminated, never reused: a worker wedged mid-conversion
    // would hold its thread and buffers for the rest of the session.
    const finish = (fn: () => void) => {
      opts.signal.removeEventListener("abort", onAbort);
      worker.terminate();
      fn();
    };
    function onAbort() {
      worker.postMessage({ type: "cancel" });
      finish(() => reject(new Error("Cancelled.")));
    }
    opts.signal.addEventListener("abort", onAbort);

    worker.onmessage = (e: MessageEvent<WorkerResponse>) => {
      const m = e.data;
      if (m.type === "progress") opts.onProgress(m.fraction);
      else if (m.type === "passthrough") finish(() => resolve(file));
      else if (m.type === "error") finish(() => reject(new Error(m.message)));
      else if (m.type === "done") {
        const name = file.name.replace(/\.[^.]+$/, "") + ".webm";
        finish(() => resolve(new File([m.buffer], name, { type: "audio/webm" })));
      }
    };
    worker.onerror = () => finish(() => reject(new Error("Couldn't extract audio from that video.")));

    worker.postMessage({ type: "start", file });
  });
}
```

- [ ] **Step 4: Verify it type-checks and nothing regressed**

Run: `cd apps/web && npm run build`
Expected: the `tsc` pass succeeds and vite builds. `AudioDecoder` is a WebCodecs global that older
TypeScript DOM lib versions do not declare; if `tsc` reports it as undefined, replace that half of the
guard with `typeof (globalThis as { AudioDecoder?: unknown }).AudioDecoder === "undefined"` rather
than adding a lib reference.

Run: `cd apps/web && npm test`
Expected: same failures as at the end of Task 1 - only `Recorder.tsx`'s `AUDIO_ACCEPT_ATTR` import.
Task 4 fixes it.

- [ ] **Step 5: Commit**

```bash
git add apps/web/package.json apps/web/package-lock.json apps/web/src/lib/videoAudio.ts apps/web/src/lib/videoAudio.worker.ts
git commit -m "feat(web): extract mono Opus audio from video in a worker via mediabunny"
```

---

### Task 4: Wire it into the UI

Connects the seam to the queue, renders the new phase with progress and a cancel control, and opens
the file picker to video.

**Watch the mock at `RecordingsPanel.test.tsx:30`:** it builds the upload context by hand. Adding
`cancel` to the context means that factory must gain `cancel: vi.fn()` or the panel renders against a
context missing a method it now expects.

**Files:**
- Modify: `apps/web/src/lib/uploadContext.tsx`
- Modify: `apps/web/src/components/nav/UploadStatusList.tsx`
- Modify: `apps/web/src/components/Recorder.tsx:50` and `:1376`
- Modify: `apps/web/src/components/RecordingsPanel.test.tsx:30`
- Modify: `apps/web/src/locales/{de,en,es,fr}/workspace.json`
- Create: `apps/web/src/components/nav/UploadStatusList.test.tsx`

**Interfaces:**
- Consumes: `extractAudio` from `../lib/videoAudio`; `UploadBatchHandle`, `UploadItem` from `./uploadQueue`
- Produces: `UploadContextValue` gains `cancel: (id: string) => void`

- [ ] **Step 1: Write the failing component test**

Create `apps/web/src/components/nav/UploadStatusList.test.tsx`:

```tsx
import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { UploadItem } from "../../lib/uploadQueue";
import UploadStatusList from "./UploadStatusList";

const item = (over: Partial<UploadItem>): UploadItem => ({
  id: "0-a.mp4",
  name: "Town Hall.mp4",
  status: "queued",
  ...over,
});

describe("UploadStatusList", () => {
  it("shows extraction progress as a percentage", () => {
    render(
      <UploadStatusList
        items={[item({ status: "extracting", progress: 0.42 })]}
        onClear={() => {}}
        onCancel={() => {}}
      />,
    );
    expect(screen.getByText(/42%/)).toBeTruthy();
  });

  it("cancels the item it belongs to", async () => {
    const onCancel = vi.fn();
    render(
      <UploadStatusList
        items={[item({ status: "extracting", progress: 0.1 })]}
        onClear={() => {}}
        onCancel={onCancel}
      />,
    );
    await userEvent.click(screen.getByRole("button", { name: /cancel/i }));
    expect(onCancel).toHaveBeenCalledWith("0-a.mp4");
  });

  it("offers no cancel once an item has settled", () => {
    render(
      <UploadStatusList items={[item({ status: "done" })]} onClear={() => {}} onCancel={() => {}} />,
    );
    expect(screen.queryByRole("button", { name: /cancel/i })).toBeNull();
  });

  it("treats a cancelled item as settled so the batch can be cleared", () => {
    render(
      <UploadStatusList
        items={[item({ status: "cancelled" })]}
        onClear={() => {}}
        onCancel={() => {}}
      />,
    );
    expect(screen.getByRole("button", { name: /clear/i })).toBeTruthy();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd apps/web && npm test -- src/components/nav/UploadStatusList.test.tsx`
Expected: FAIL - `onCancel` is not a prop, and there is no percentage text.

- [ ] **Step 3: Add the four locale keys**

In each of `apps/web/src/locales/{en,de,es,fr}/workspace.json`, beside the existing `uploadQueued`
block, add:

`en`:
```json
  "uploadExtracting": "Extracting audio",
  "uploadCancelled": "Cancelled",
  "uploadCancel": "Cancel",
  "recUploadTitle": "Upload audio or video files to transcribe (or drag them onto the recordings panel)",
```

`de`:
```json
  "uploadExtracting": "Audio wird extrahiert",
  "uploadCancelled": "Abgebrochen",
  "uploadCancel": "Abbrechen",
  "recUploadTitle": "Audio- oder Videodateien zum Transkribieren hochladen (oder auf die Aufnahmeliste ziehen)",
```

`es`:
```json
  "uploadExtracting": "Extrayendo audio",
  "uploadCancelled": "Cancelado",
  "uploadCancel": "Cancelar",
  "recUploadTitle": "Sube archivos de audio o vídeo para transcribir (o arrástralos al panel de grabaciones)",
```

`fr`:
```json
  "uploadExtracting": "Extraction de l'audio",
  "uploadCancelled": "Annulé",
  "uploadCancel": "Annuler",
  "recUploadTitle": "Importer des fichiers audio ou vidéo à transcrire (ou les glisser sur le panneau des enregistrements)",
```

`recUploadTitle` already exists in all four - replace its value rather than adding a duplicate key.

- [ ] **Step 4: Update the status list**

Replace `apps/web/src/components/nav/UploadStatusList.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import type { UploadItem } from "../../lib/uploadQueue";

/// Per-file status for the current upload batch. Tolerant of partial failures - a rejected file shows
/// its reason and the rest still upload. A video is extracted before it uploads, which on a long
/// recording takes minutes, so that phase shows progress and can be cancelled.
function UploadStatusList({
  items,
  onClear,
  onCancel,
}: {
  items: UploadItem[];
  onClear: () => void;
  onCancel: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  if (items.length === 0) return null;
  const isSettled = (s: UploadItem["status"]) => s === "done" || s === "failed" || s === "cancelled";
  const settled = items.every((i) => isSettled(i.status));
  const tag: Record<UploadItem["status"], string> = {
    queued: "text-gray-400",
    extracting: "text-amber-600 dark:text-amber-400",
    uploading: "text-amber-600 dark:text-amber-400",
    done: "text-green-600 dark:text-green-400",
    failed: "text-red-600 dark:text-red-400",
    cancelled: "text-gray-400",
  };
  const label: Record<UploadItem["status"], string> = {
    queued: t("uploadQueued"),
    extracting: t("uploadExtracting"),
    uploading: t("uploadUploading"),
    done: t("uploadDone"),
    failed: t("uploadFailed"),
    cancelled: t("uploadCancelled"),
  };
  return (
    <div className="border-b px-3 py-2 dark:border-gray-800">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400">{t("uploadsHeader")}</span>
        {settled && (
          <button type="button" onClick={onClear} className="text-xs text-gray-400 hover:underline">
            {t("clear")}
          </button>
        )}
      </div>
      <ul className="space-y-0.5">
        {items.map((i) => (
          <li key={i.id} className="flex items-center justify-between gap-2 text-xs">
            <span className="truncate dark:text-gray-300" title={i.name}>{i.name}</span>
            <span className="flex shrink-0 items-center gap-1.5">
              <span className={tag[i.status]} title={i.error}>
                {label[i.status]}
                {i.status === "extracting" && i.progress !== undefined
                  ? ` ${Math.round(i.progress * 100)}%`
                  : ""}
              </span>
              {!isSettled(i.status) && (
                <button
                  type="button"
                  onClick={() => onCancel(i.id)}
                  className="text-gray-400 hover:underline"
                >
                  {t("uploadCancel")}
                </button>
              )}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
export default UploadStatusList;
```

- [ ] **Step 5: Run the component test to verify it passes**

Run: `cd apps/web && npm test -- src/components/nav/UploadStatusList.test.tsx`
Expected: PASS, 4 tests.

- [ ] **Step 6: Mutation-verify the settled test**

In `isSettled`, remove `|| s === "cancelled"`. Run the test.
Expected: FAIL on "treats a cancelled item as settled so the batch can be cleared". **Restore.**

- [ ] **Step 7: Wire the context**

In `apps/web/src/lib/uploadContext.tsx`:

Add to the imports:
```ts
import { runUploadBatch, type UploadBatchHandle, type UploadItem } from "./uploadQueue";
import { extractAudio } from "./videoAudio";
```
and add `useRef` to the existing `react` import.

Add to `UploadContextValue`, after `uploadFiles`:
```ts
  /// Cancel one in-flight item (an extraction in progress). Settled items ignore it.
  cancel: (id: string) => void;
```

Add `cancel: () => {},` to the default context object.

Inside `UploadProvider`, add above `uploadFiles`:
```ts
  // The running batch's cancel handle, replaced each time a batch starts.
  const handleRef = useRef<UploadBatchHandle | null>(null);
```

Add these three deps to the `runUploadBatch({ ... })` call, after `onSuccess`:
```ts
        extract: extractAudio,
        onHandle: (h) => {
          handleRef.current = h;
        },
```

Add below `clearFinished`:
```ts
  const cancel = useCallback((id: string) => handleRef.current?.cancel(id), []);
```

Change `clearFinished` to keep the in-flight statuses, which now include extraction:
```ts
  const clearFinished = useCallback(
    () =>
      setItems((it) =>
        it.filter((i) => i.status === "queued" || i.status === "extracting" || i.status === "uploading"),
      ),
    [],
  );
```

And add `cancel` to the provider value:
```tsx
    <UploadContext.Provider value={{ items, busy, uploadFiles, clearFinished, cancel }}>
```

- [ ] **Step 8: Open the picker to video**

In `apps/web/src/components/Recorder.tsx`, change line 50 from
```ts
import { AUDIO_ACCEPT_ATTR } from "../lib/audioFormats";
```
to
```ts
import { MEDIA_ACCEPT_ATTR } from "../lib/mediaKinds";
```
and line 1376 from `accept={AUDIO_ACCEPT_ATTR}` to `accept={MEDIA_ACCEPT_ATTR}`.

In `apps/web/src/components/RecordingsPanel.tsx:157`, update the stale comment - it says "Drag audio
files anywhere onto the panel":
```ts
  // Drag audio or video files anywhere onto the panel to upload them (a video has its audio extracted
  // in the browser first, so the video itself is never sent). Distinct from the reorder DnD, which uses
```

- [ ] **Step 9: Repair the hand-built context mock**

In `apps/web/src/components/RecordingsPanel.test.tsx:30`, add `cancel` to the factory:
```ts
  useUpload: () => ({ items: [], busy: false, uploadFiles: uploadFilesMock, clearFinished: vi.fn(), cancel: vi.fn() }),
```

Then find every render site of `UploadStatusList` (`rg "UploadStatusList" apps/web/src`) and pass the
new `onCancel={upload.cancel}` prop from the upload context.

- [ ] **Step 10: Run the full suite and the build**

Run: `cd apps/web && npm test`
Expected: PASS, no failures. In particular `locales.test.ts` must pass, proving the four keys landed
in all four catalogs with no empty values.

Run: `cd apps/web && npm run build`
Expected: clean `tsc` then a successful vite build.

- [ ] **Step 11: Commit**

```bash
git add apps/web/src/lib/uploadContext.tsx apps/web/src/components/nav/UploadStatusList.tsx apps/web/src/components/nav/UploadStatusList.test.tsx apps/web/src/components/Recorder.tsx apps/web/src/components/RecordingsPanel.tsx apps/web/src/components/RecordingsPanel.test.tsx apps/web/src/locales
git commit -m "feat(web): accept video drops, showing extraction progress with cancel"
```

---

### Task 5: Browser verification

jsdom computes no media and has no WebCodecs, so nothing so far proves extraction actually works.
This task is where the feature is really tested. **Do not skip it and do not claim the feature works
without it.**

**Files:** none changed unless a check fails.

- [ ] **Step 1: Start the stack and the dev server**

The API, Postgres, Redis, MinIO and the GPU worker come from Docker; the web app runs from Vite so
the worker bundle rebuilds on edit.

```bash
cd deploy && docker compose up -d
```

Then start the Vite dev server through the preview tooling (never `npm run dev` in a raw shell) and
sign in.

- [ ] **Step 2: The main case - a real webinar-sized MP4**

Drop a genuine MP4 of roughly 1 GB onto the recordings panel. Confirm, in order:
1. the row reads "Extracting audio" with a percentage that climbs;
2. **the UI stays responsive throughout** - scroll the list while it runs. If it freezes, the worker
   is not being used and Task 3 Step 3 is wrong;
3. the row switches to "Uploading", then "Done";
4. the new recording's size in the list is tens of MB, not hundreds - proof the video was discarded;
5. it transcribes to a normal speaker-labelled transcript;
6. its audio plays back in the recording page and sounds full-band, not muffled.

- [ ] **Step 3: The other four checks**

- **`.mkv`** - drop one, confirm it extracts and transcribes.
- **Audio-only `.webm`** - download one of your own existing Diariz recordings and drop it back in.
  It must go **straight to "Uploading"** with no "Extracting audio" phase at all, and the uploaded
  size must match the original byte-for-byte. Any extraction here means the passthrough branch is
  broken and ordinary re-uploads are being re-encoded.
- **Audio-only `.mp4` with cover art** - build one with
  `ffmpeg -i in.mp3 -i cover.jpg -map 0 -map 1 -c copy -disposition:v attached_pic out.mp4`, drop it,
  and confirm it passes through rather than extracting. If it extracts, the `videoSeconds <= 1` guard
  needs the fallback: compare against `await input.computeDuration()` and treat a video track shorter
  than 10% of the file as artwork.
- **Cancel** - drop the 1 GB MP4 again, click Cancel mid-extraction. The row must read "Cancelled",
  no recording may appear in the list, and the tab's memory must fall back (check the browser task
  manager) proving the worker was terminated rather than left running.

- [ ] **Step 4: Record the evidence**

Screenshot the extraction phase in progress and the resulting recording's size in the list. These go
in the PR body as proof, not as a claim.

- [ ] **Step 5: Commit any fixes**

If any check failed, fix it, re-run the affected unit tests, and commit. If all five passed with no
code change, there is nothing to commit - say so rather than inventing a commit.

---

### Task 6: Release checklist, docs, and the PR

Every user-facing PR in this repo ships exactly one release. This is a functional enhancement, so
Minor +1 with Build reset.

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`, `apps/web/src/components/AboutModal.tsx`
- Modify: `README.md:23`, `docs/features.md:21-24`
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Modify: `apps/web/src/content/help/en/uploading-audio.md`

- [ ] **Step 1: Bump the version in all five places**

Set `0.209.0` in `version.json`, the `"version"` field of `apps/web/package.json`,
`apps/desktop/package.json`, and `integrations/n8n-nodes-diariz/package.json`, and the `<Version>`
element of `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 2: Verify the mirrors before writing anything else**

Run: `cd apps/web && npm test -- src/lib/versionMirrors.test.ts`
Expected: PASS. This test exists because the n8n node silently sat at `0.1.0` for about seventy
releases and an npm version cannot be corrected once published.

- [ ] **Step 3: Update the About-box capability row**

In `apps/web/src/lib/releases.ts`, the `CAPABILITIES` table's **Capture** row (line 17) ends with the
upload list. Replace `or upload files (WAV, MP3, FLAC, Ogg/Opus, WebM, M4A)` with:

```
or upload files (WAV, MP3, FLAC, Ogg/Opus, WebM, M4A) - or drop a **video** (MP4, MOV, MKV, WebM) and its audio is extracted in your browser and uploaded on its own, so the video is never sent or stored
```

- [ ] **Step 4: Add the release entry**

Insert at the top of `RELEASES` in `apps/web/src/lib/releases.ts`, above the `0.208.0` entry. Leave
`pr` out for now - Step 9 fills it in once the real number exists. Guessing "last + 1" fails here,
because Dependabot PRs and issues share the same number sequence.

```ts
  {
    version: "0.209.0",
    date: "2026-08-12",
    headline: "Drop a video, get a transcript",
    summary:
      "Recorded webinars and town halls usually arrive as video, and until now you had to extract the " +
      "audio yourself before Diariz would take it. You can now drop an MP4, MOV, MKV or WebM straight " +
      "onto the meetings list. The audio is pulled out **in your browser**, mixed down to mono, and " +
      "uploaded on its own - the video never leaves your machine and is never stored, so a 3 GB " +
      "recording becomes a ~50 MB upload that barely touches your quota. Long files show extraction " +
      "progress and can be cancelled. Audio files you upload are untouched, and re-uploading a " +
      "recording Diariz made is passed straight through without being re-encoded.",
    added: [
      "Drag or pick a video file (MP4, MOV, MKV, WebM) to transcribe its audio.",
      "Per-file extraction progress with a Cancel control while a long video is being processed.",
    ],
    changed: [
      "A WebM screen recording now has its video track discarded before upload, rather than being stored whole.",
    ],
  },
```

- [ ] **Step 5: Verify the release data gates**

Run: `cd apps/web && npm test -- src/lib/releases.test.ts`
Expected: PASS - `RELEASES[0].version` now equals `version.json`.

- [ ] **Step 6: Add the licence disclaimer**

In `apps/web/src/components/AboutModal.tsx`, add `mediabunny` to the open-source list on line 76-78,
after `DOMPurify`, and add a paragraph after the existing ones:

```tsx
          <p className="mt-2">
            Extracting audio from a dropped video happens entirely in your browser using{" "}
            <strong>mediabunny</strong> (MPL-2.0) and the browser's own WebCodecs decoders. The video
            is never uploaded and never stored.
          </p>
```

- [ ] **Step 7: Update the README and features doc in lockstep**

`README.md:23`, in the **Capture** row, replace
`upload files (WAV/MP3/FLAC/Ogg/Opus/WebM/M4A) or drag-drop several at once, landing in the folder you drop them on.`
with
`upload files (WAV/MP3/FLAC/Ogg/Opus/WebM/M4A) or drop a video (MP4/MOV/MKV/WebM) and its audio is extracted in the browser so the video is never uploaded; drag-drop several at once, landing in the folder you drop them on.`

`docs/features.md:21-24`, extend the upload sentence. Note line 21 currently contains an em-dash
("M4A) — via the") - replace it with a plain hyphen while you are in there:

```
Or **upload existing audio files** to transcribe (WAV, MP3, FLAC, Ogg/Opus, WebM, M4A) - via the
Upload button or by **dragging several onto the recordings list**, with per-file status. You can also
drop a **video** (MP4, MOV, MKV, WebM): its audio is extracted **in your browser**, mixed to mono and
uploaded on its own, so the video is never sent to the server or stored against your quota. Long
extractions show progress and can be cancelled. Dropped files land in **the folder the list is
showing** - where you dropped them beats the placement preference, which decides for the Upload
button (and for a new recording) instead.
```

- [ ] **Step 8: Update the architecture doc and the help article**

In `docs/Overall_Synopsis_of_Platform.md`, in the recording-upload section that lists the four size
limits, add a paragraph recording that video files are converted client-side before upload (mediabunny
in a Web Worker; video discarded; mono Opus 48 kHz WebM), that this is a **web/desktop UI guarantee
rather than a server invariant**, and that a direct API/n8n/MCP caller posting an MP4 still has it
stored because `AudioFormats.Detect` sniffs `ftyp` as `m4a`.

Rewrite `apps/web/src/content/help/en/uploading-audio.md` - ASCII only, keeping the front-matter
block, with the `summary` at two to three sentences:

```markdown
---
title: Uploading audio and video files
summary: Upload recordings you already have. Diariz accepts WAV, MP3, FLAC, Ogg/Opus, WebM, and M4A, and you can drop a video (MP4, MOV, MKV) to transcribe its audio. Video is converted in your browser, so only the audio is ever uploaded.
group: getting-started
order: 30
---

Use the **Upload** button above the meetings list, or drag one or more files straight onto the
list. Each file gets its own progress and status, so a large batch is easy to watch.

## Supported formats

Audio: WAV, MP3, FLAC, Ogg/Opus, WebM, and M4A/AAC.

Video: MP4, MOV, MKV, and WebM.

Diariz identifies the format by inspecting the file's actual contents, not by trusting its extension,
so a mislabelled file is rejected rather than silently failing later in the pipeline. M4A/AAC support
is a server setting an administrator can turn off, because that codec is patented.

There is a maximum upload size (500 MB by default) and a maximum audio duration, both set by the
server. For a video, the limit applies to the extracted audio, not to the video file - so a 3 GB
recording is fine, because what gets uploaded is usually well under 100 MB.

## Dropping a video

Only the audio is ever uploaded. When you drop a video, Diariz extracts its sound track in your
browser, mixes it down to mono, and sends just that. The video itself never leaves your machine and
is never stored, so it costs you nothing against your storage quota.

A long recording takes a little while to process, so the file's row shows "Extracting audio" with a
percentage, and a **Cancel** link if you change your mind. Nothing is uploaded until extraction has
finished.

Audio files are not affected: they upload exactly as they always have. Re-uploading a recording that
Diariz itself made is passed straight through without being processed again.

This works in Chrome, Edge, and the desktop app. A browser without the necessary media support says
so rather than uploading the video.

## What happens next

An uploaded file goes through exactly the same pipeline as a recording made in the app: transcription,
speaker separation, and then whichever AI steps are configured. Diariz measures the true duration of
the audio itself, so the meeting shows the correct length even though the upload carried no timing
information.

## If an upload fails

The most common causes are a format the server does not accept, a file over the size limit, or audio
longer than the configured maximum. The per-file status tells you which. Re-encoding to WAV or MP3
resolves most format problems.

A video can also fail because it has no sound track at all, or uses an audio codec your browser
cannot decode. Diariz never falls back to uploading the video itself, so you will get a clear message
instead of a surprise upload.
```

- [ ] **Step 9: Run every gate, then open the PR**

```bash
cd apps/web && npm test && npm run build
```
Expected: PASS throughout - including `helpContent.test.ts` (front-matter and ASCII), `locales.test.ts`,
`releases.test.ts`, and `versionMirrors.test.ts`.

Check no fancy dashes were introduced in what you touched:

```bash
git diff --name-only main | xargs rg -n '[–—]' || echo "clean"
```

Then commit and open the PR:

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts apps/web/src/components/AboutModal.tsx README.md docs/features.md docs/Overall_Synopsis_of_Platform.md apps/web/src/content/help/en/uploading-audio.md
git commit -m "chore(release): 0.209.0 - drop a video, get a transcript"
git push -u origin feat/video-audio-extraction
gh pr create --title "Drop a video, get a transcript" --body "..."
```

The PR body must state: what changed, the browser-verification evidence from Task 5 (with the
screenshots), and the deployment surface - **server redeploy only, no desktop release**, because
nothing under `apps/desktop/src`, `apps/desktop/build`, or `electron-builder.config.js` was touched.

- [ ] **Step 10: Backfill the PR number**

Take the number `gh pr create` printed, add `pr: <number>,` to the `0.209.0` entry in `releases.ts`,
then commit and push:

```bash
git add apps/web/src/lib/releases.ts
git commit -m "docs: record the PR number in the release entry"
git push
```

---

## Self-Review

**Spec coverage.** Every section of the design maps to a task: §4.3 classification and §4.5 the size
split to Task 1; §4.4 the queue phase to Task 2; §4.7 the worker seam and §4.8 the dependency to
Task 3; §4.6's passthrough peek to Task 3 Step 2 with its browser proof in Task 5; the §5 error table
to Tasks 2-4 and verified in Task 5; §6 testing across Tasks 1-5; §7 the release surface to Task 6.
§3's non-goals are respected - no server change, no new `RecordingSource`, no confirmation dialog -
and §8's deferred server-side hole is written into the architecture doc in Task 6 Step 8 rather than
being quietly dropped.

**Type consistency.** `ExtractOptions` is declared in `uploadQueue.ts` (Task 2) and imported by
`videoAudio.ts` (Task 3); `extractAudio`'s signature matches `RunUploadBatchDeps["extract"]` exactly.
`UploadBatchHandle` is produced in Task 2 and consumed in Task 4. `MediaKind`, `classifyFile`,
`sourceProblem`, and `resultProblem` are produced in Task 1 and consumed in Task 2. The four new
locale keys added in Task 4 Step 3 are exactly the four read by the component in Step 4.

**Known rough edge, deliberately left in.** Task 3's `bitrate: 32e3` and `videoTrack.computeDuration()`
are written from mediabunny's published API; if the installed build differs, Task 3 Step 4's build and
Task 5's browser checks are what catch it, and each has its concrete fallback written inline rather
than left as an exercise.
