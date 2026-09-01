# Streaming Capture and Live Transcript - Feature Specification

**Status:** specified, gated on one spike (§5.1). Throughput question **measured and closed** (§3).
**Surface:** web recorder (`apps/web`), API (`src/Diariz.Api`), worker (`src/Diariz.Worker`), domain (`src/Diariz.Domain`)
**Server changes:** yes - new entity, new status, new Redis stream, new endpoints, new callback
**Desktop release:** not required (the shell is untouched; the recorder lives in the web app)

---

## 1. What the user asked for

> "Uploading the transcript in chunks or even streamed to the server - i.e. at some interval during
> the meeting, creating the recording record as the record button is pressed and then updating it as
> the meeting progresses. Reasoning being I would like in future to have the transcript and chat
> function available to me during the meeting for contemporaneous analysis of what has been said."

Two capabilities, related but separable:

1. **Streaming capture.** The recording exists server-side from the moment Record is pressed, and grows
   as the meeting runs, instead of appearing in one multipart POST at Stop.
2. **Live transcript.** That growing audio is transcribed as it arrives, so the transcript - and chat
   over it - are usable while the meeting is still happening.

(1) is worth shipping on its own and carries no AI risk. (2) is the point, and depends on (1).

---

## 2. Scope of the change against what exists today

Today the whole capture path is a single deferred act. `apps/web/src/components/Recorder.tsx` accumulates
every `MediaRecorder` chunk in `chunksRef` (line 326), assembles one `Blob` at `stop()` (line 1104), and
POSTs it to `RecordingsController.Upload`. Nothing reaches the server until the meeting is over. The
consequences are all downstream of that one fact:

| Today | Cause |
|---|---|
| A three-hour meeting holds ~170 MB of Blobs in the tab | `chunksRef` grows for the whole recording |
| A closed laptop lid loses the entire meeting | Audio only leaves the browser at Stop (`pendingRecording.ts` stashes to IndexedDB *after* stop, which does not help if the tab dies mid-meeting) |
| Long meetings end with a single large upload | One multipart POST capped by `Uploads:MaxBytes` |
| No transcript exists until after the meeting | No audio on the server until after the meeting |

This spec changes only the transport and adds a provisional transcription pass. **The canonical
transcript is produced exactly as it is today** - one full-file pass through the existing worker,
`internal/transcriptions/result`, and every downstream job (summary, minutes, actions, tags,
embeddings) unchanged.

---

## 3. The measurement that gates this

Live chunked transcription is only possible if a chunk costs materially less wall-clock time than its
own duration. If a 30 s chunk costs 45 s, the backlog grows without bound for the entire meeting and
the transcript falls further behind the longer the meeting runs - a broken feature, not a slow one.

This was measured on **2026-08-30** against the production CUDA box (RTX 5090) and a second box
(RTX 4070 Laptop, 8 GB) for contrast. Method and reproduction are in §16.

### 3.1 RTX 5090 - the production box

End-to-end through the real Redis queue, timed by the worker's own `Transcription.ProcessingMs`
(blob download + ASR + alignment + diarization + voiceprint embedding). Median of three passes.

| Clip | Processing | xRealtime | Queue wait | Segments | Speakers found |
|---:|---:|---:|---:|---:|---:|
| 5 s | 0.72 s | 0.14x | 1.3 s | 1 | 1 |
| 10 s | 1.01 s | 0.10x | 1.0 s | 1 | 1 |
| 15 s | 1.40 s | 0.09x | 0.7 s | 3 | 1 |
| **20 s** | **2.15 s** | **0.11x** | 1.7 s | 4 | 3 |
| **30 s** | **2.67 s** | **0.09x** | 1.7 s | 6 | 2 |
| **45 s** | **2.76 s** | **0.06x** | 1.2 s | 10 | 2 |
| 60 s | 3.68 s | 0.06x | 0.6 s | 13 | 2 |
| 90 s | 4.01 s | 0.04x | 0.3 s | 19 | 2 |
| 120 s | 4.94 s | 0.04x | 1.2 s | 27 | 2 |
| 180 s | 6.36 s | 0.04x | 1.3 s | 41 | 2 |
| 240 s | 8.05 s | 0.03x | 1.0 s | 57 | 2 |

Least-squares fit: **1.26 s fixed + 0.029 s per second of audio**, accurate to within 2% at the long
end. The bold rows are the plausible live-chunk sizes. **A 30 s chunk costs 2.67 s measured - 91%
headroom against its own duration.**

### 3.2 Validated against real recordings

Synthetic speech is cleaner than a meeting (no overlap, no room noise, few speakers), so the fit was
checked against **58 real recordings already on the instance** - median 62 min, longest 227 min, up to
49 speakers, 73.5 hours in total. Aggregate statistics only; no recording content, title, or
participant was read.

| Statistic | Realtime factor |
|---|---:|
| p10 | 0.0183 |
| **median** | **0.0205 (~49x realtime)** |
| p90 | 0.0329 |
| worst observed | 0.0482 (~21x realtime) |

Real audio is **cheaper** per second than the synthetic ladder, not dearer - the ladder is dominated by
the fixed floor at short lengths. There is no hidden difficulty in real meetings that the synthetic
test missed.

### 3.3 RTX 4070 Laptop, 8 GB - the hardware floor

Run directly inside the worker image with per-stage timing, which is what shows where the cost sits.

| Clip | Total | xRealtime | ASR | Diarize | Align | Embeddings |
|---:|---:|---:|---:|---:|---:|---:|
| 5 s | 0.84 s | 0.17x | 0.72 s | 0.04 s | 0.03 s | 0.01 s |
| 10 s | 1.03 s | 0.10x | 0.85 s | 0.05 s | 0.05 s | 0.02 s |
| 15 s | 1.54 s | 0.10x | 1.18 s | 0.21 s | 0.08 s | 0.02 s |
| 20 s | 13.50 s | 0.68x | 2.95 s | 10.36 s | 0.11 s | 0.03 s |
| 30 s | 24.36 s | 0.81x | 5.04 s | 19.07 s | 0.17 s | 0.02 s |
| 45 s | 37.98 s | 0.84x | 8.80 s | 28.80 s | 0.28 s | 0.03 s |
| 60 s | 48.41 s | 0.81x | 9.47 s | 38.47 s | 0.35 s | 0.04 s |
| 90 s | 83.99 s | 0.93x | 16.28 s | 67.07 s | 0.51 s | 0.06 s |
| 120 s | 118.63 s | 0.99x | 22.03 s | 95.71 s | 0.72 s | 0.08 s |
| 180 s | 191.33 s | **1.06x** | 32.79 s | 153.61 s | 3.64 s | 1.18 s |
| 240 s | 253.48 s | **1.06x** | 44.26 s | 201.79 s | 4.99 s | 2.31 s |

Three findings, all load-bearing for the design:

- **Diarization is 75-80% of every chunk.** ASR is a quarter; alignment and voiceprint embedding are
  rounding errors. The proportion holds on the 5090; only the absolute numbers change.
- **Short clips are cheap for the wrong reason.** The 5-15 s clips contain one speaker, so pyannote's
  clustering has nothing to do. The step at 20 s is where the second voice appears, **not** a duration
  threshold. Any chunk-size decision must read the 20 s row, never the 15 s one.
- **8 GB is not enough.** The card sat at 7804 of 8188 MiB (95% of VRAM) at 100% utilisation, crossed
  1.0x realtime at about 150 s, and settled at 1.06x. Alignment and embedding - free on the 5090 and
  free here up to 120 s - start costing whole seconds on the longest clips, which is the signature of
  memory pressure rather than a slow card. Cold start into VRAM was 57.8 s.

Both boxes produced **identical segment counts and speaker labels at every clip length**, which is what
makes the comparison trustworthy: the fast box is doing the same work, not skipping diarization.

A single straight-line fit is valid for the 5090 but **not** for the 4070, whose curve has a step change
at 20 s; fitting one line across it yields a negative intercept, which is an artefact of the regime
change rather than a floor.

### 3.4 What the measurement decides

| Question | Answer |
|---|---|
| Can a chunk be transcribed faster than realtime? | Yes, by 10-30x on the production box. |
| Must the live pass use a smaller model? | No. `large-v3` is fine. |
| Must the live pass use a hosted `/audio/transcriptions` endpoint? | No - which also means audio never leaves the deployment. |
| Does live need a second worker process holding a second copy of the weights? | No (§6.3). |
| Can the live pass afford diarization? | Yes. A diarized 30 s chunk costs 2.67 s. |
| Can this run on any GPU? | **No.** 8 GB cannot. See §12.4. |

---

## 4. Correction to an existing figure

The only worker throughput number recorded anywhere in the repo is
`docs/Overall_Synopsis_of_Platform.md`'s **"~1.3-1.7x realtime for `large-v3`"**. That figure is correct
but is measured on the **ROCm Strix Halo box** running the `openai-whisper` backend on an APU. It is
**25-50x slower than the CUDA path** and must not be used to reason about the CUDA deployment.

Consequence for this feature's rationale: on the CUDA box a 60-minute meeting occupies the worker for
roughly **75 seconds**, so the transcript is readable about a minute and a half after Stop - not the
~40 minutes the ROCm figure implies. **The value of this feature is therefore entirely in-meeting use.**
It does not meaningfully shorten the wait afterwards, and should not be justified on that basis.

`Overall_Synopsis_of_Platform.md` is updated in the same PR to attribute its figure to the ROCm box and
record the CUDA one beside it.

---

## 5. Decisions taken

### 5.1 D1 - chunk framing (**gated on a spike**)

`MediaRecorder.start(timeslice)` emits chunks, but only the first carries the EBML header and Opus
codec-private data; chunks two onward are not independently decodable by ffmpeg.

| Option | Canonical audio | Live decode | Verdict |
|---|---|---|---|
| **A. Fragment + header prepend** - `start(timeslice)`, store every fragment, concatenate all fragments byte-wise for the canonical file, and prepend fragment 0 when decoding a live window | Byte-identical to today | Needs fragment 0 prepended | **Preferred**, subject to spike |
| **B. Stop/restart per chunk** - each blob self-contained, as `dictationEngine.ts` `createServerEngine` already does | Loses a few ms per restart (~0.6 s scattered over a 60-min meeting at 30 s chunks) | Works unmodified | **Documented fallback** |
| C. Raw PCM from an AudioWorklet | Lossless, but ~84 MB/hour against ~11 MB for Opus | Trivial | Rejected: 8x the bytes for a problem A or B already solves |

**Spike S0 must run before anything else in Phase 1** and answers one question: does byte-concatenating
`start(timeslice)` fragments reproduce a file ffmpeg decodes identically to the single-blob recording,
and does `fragment[0] + fragment[k..n]` decode correctly, on Chrome, Firefox, Safari and Electron? If
any browser says no, the spec falls back to option B and §7.2 gains a note that the canonical audio has
sub-second scattered gaps.

Option B is known to work - it is in production for dictation - so the spike is about whether A's
better canonical audio is available, not about whether the feature is possible.

#### 5.1 findings (S0, run 2026-08-30)

**Verdict: adopt option A**, with one amendment to §6.1 that the spike turned up (see "the finding that
changes the design" below).

Method: a 12 s signal synthesised in Web Audio and routed through a `MediaStreamAudioDestinationNode`
rather than captured from a microphone - no permission prompt, and a deterministic input, so any
difference in the output is the container's doing. Recorded twice per engine: once with `start()` and
once with `start(2000)`. Decoded with ffmpeg 9.0.1 to 48 kHz mono `s16le` and measured; the browser's
own `decodeAudioData` agreed with ffmpeg exactly in every case.

| Artefact | Bytes | ffmpeg decode | Verdict |
|---|---:|---|---|
| `single.webm` (today's path) | 192,530 | 11.94 s, clean | baseline |
| 6 fragments byte-concatenated | 193,441 | **12.00 s, clean** | **A works** |
| `fragment[0] + fragment[2..5]` | 160,581 | **9.96 s, clean** | **later windows work** |
| `fragment[1]` alone | 32,860 | *EBML header parsing failed* | as expected |

The last row matters: a bare fragment genuinely does not decode, so the first three rows are not
passing vacuously.

**Engines tested:** Chromium 148.0.7778.280 and Electron 43.2.0 (the version `apps/desktop` ships).
Both produced **byte-identical fragment sizes** (32040, 32860, 32860, 32860, 32860, 29961) and
identical decodes, which is unsurprising given the shared media stack but is now measured rather than
assumed.

**Not tested: Firefox and Safari.** Neither was available on the machine (no Firefox installed, no Mac).
This is a real coverage gap, not a pass. Both are Gecko/WebKit rather than Chromium and neither shares
the tested muxer, so **the recorder must feature-detect at runtime rather than assume A holds
everywhere**: on first use, byte-concatenate the first two fragments, attempt a decode, and fall back
to option B for that session if it fails. That check is cheap and removes the need to re-run this spike
per browser release. Carry it into the implementation as an explicit task.

**The finding that changes the design.** `audio_merge.build_concat_command` **cannot be reused over raw
fragments.** It uses the concat *filter* with one `-i` per input, so ffmpeg opens every input
independently and dies on the second one:

```
[in#1] EBML header parsing failed
[in#1] Error opening input: Invalid data found when processing input
```

The fix is small and stays inside the worker: **byte-join the chunk blobs into a single file first, then
pass that one file** to the existing command. Verified - the re-encoded output decodes to 12.00 s and
`ffprobe` reports a proper `11.999` duration, so `probe_duration_ms` keeps working unchanged. §6.1's
"reuse the existing `audio-merge-jobs` path" therefore needs a new worker-side entry point that byte-joins
before concatenating, not merely a `Kind` flag routed in the API.

Related and worth knowing, though not a problem in itself: `ffprobe` reports `duration=N/A` for a
*raw* byte-concatenation, because the streaming muxer never writes a Duration element. Nothing in the
pipeline probes a raw chunk today - `probe_duration_ms` only ever sees the re-encoded output - but any
future code that probes a chunk directly will get `N/A` rather than an error, which is the kind of thing
that fails silently.

### 5.2 D2 - overlap is a property of the decode window, not of the stored chunks

Whisper on a clip that starts or ends mid-sentence produces noise at the seams. The fix is overlapping
transcription windows, but overlapping *stored* chunks would duplicate audio and break the concat that
produces the canonical file.

**Resolution:** chunks are stored contiguous and non-overlapping. The live job carries the *previous*
chunk's key, and the worker prepends `Live:OverlapMs` of its tail before transcribing. Results outside
the chunk's own time range are discarded before they are persisted. Overlap costs nothing on disk and
nothing in the concat path, and the 91% headroom pays for the extra audio comfortably.

### 5.3 D3 - the live transcript is provisional and disposable

The live pass writes a `Transcription` with `IsProvisional = true`. The final pass, after Stop, writes
the next version in the normal way. `RecordingsController.Get` already returns only the highest version,
so the provisional transcript is superseded automatically the moment the real one lands, and is retained
for comparison exactly as any older version is.

This is what keeps the change additive:

- Nothing downstream reconciles partial results.
- Seam errors and imperfect speaker labels never reach the record.
- Summary, minutes, actions, tags and embeddings never run on provisional text (§7.2).
- The final pass costs ~75 s on an hour of audio, so running it in full is not a sacrifice worth
  optimising away.

### 5.4 D4 - one recorder, not two

An earlier draft proposed a second `MediaRecorder` on the same `MediaStream` so the canonical file
stayed byte-identical while a separate chunked recorder fed the live path. **Rejected**, because with
D1 option A the canonical file is already byte-identical, and a second recorder would double upload
bytes and storage while deleting Phase 1's durability benefit (the canonical audio would still only
arrive at Stop). If spike S0 forces option B *and* the scattered gaps prove audible, revisit this.

### 5.5 D5 - no live RAG

`EmbeddingProcessor` replaces a recording's `TranscriptChunk` rows wholesale, so re-running it every
30 s would re-embed the whole meeting repeatedly. Chat over a live meeting pre-loads the transcript
instead: an hour of talk is roughly eight to ten thousand tokens, well inside the context budget
`ChatContextMeter` already manages. Retrieval is for the library; the meeting you are sitting in fits
in the window.

---

## 6. Design

### 6.1 Lifecycle

```
Record pressed
  POST /api/recordings/live            -> Recording{Status=Live}, provisional Transcription v1
  (repeat, every ~20-45 s)
  PUT  /api/recordings/{id}/chunks/{n} -> blob to MinIO, RecordingChunk row, enqueue live-chunk job
                                          worker -> internal/transcriptions/live-chunk
                                          -> Segments appended to v1, SignalR LiveTranscriptAppended
Stop pressed
  POST /api/recordings/{id}/live/finalize
                                       -> Status=Merging, enqueue audio-merge job over the chunks
                                          worker concat -> canonical blob swapped onto the Recording
                                          -> enqueue normal transcription job (v2, final)
                                          -> existing callback path, unchanged from here on
```

### 6.2 New Redis stream

A twelfth queue, `live-chunk-jobs`, consumed by the **Python worker** (its fourth stream, same
`workers` consumer group, alongside `transcription-jobs`, `audio-merge-jobs` and `voiceprint-jobs`).

Job payload, PascalCase per the existing cross-boundary contract:

```
LiveChunkJob {
  RecordingId, TranscriptionId, Sequence, BlobKey,
  PrevBlobKey,        // null for sequence 0
  OffsetMs,           // chunk start, relative to the recording
  OverlapMs,          // how much of PrevBlobKey to prepend
  Language            // resolved the same way EnqueueTranscriptionAsync resolves it
}
```

Callback `POST internal/transcriptions/live-chunk`, `X-Worker-Secret` as usual:

```
LiveChunkResult {
  RecordingId, TranscriptionId, Sequence, Language,
  Segments: [{ Speaker, StartMs, EndMs, Text, Words }],   // already offset into recording time
  Speakers: [{ Speaker, Embedding }],                     // 192-d ECAPA, as the existing callback
  ProcessingMs
}
```

### 6.3 Worker loop: priority read

`run_loop` currently issues one blocking `XREADGROUP` across its streams with `count=1` and handles jobs
serially. Adding a fourth stream naively would let a live chunk queue behind a full-meeting job.

**Change:** a non-blocking `XREADGROUP` on `live-chunk-jobs` first; fall through to the existing blocking
multi-stream read only when it is empty. That bounds live latency by the *in-flight* job's remaining
time rather than by queue depth.

A live chunk can still wait behind one in-flight full-meeting transcription. Measured, that is ~75 s for
a 60-minute recording - a visible stall, but bounded and rare. A second worker process is **not**
specified; if production telemetry shows it biting, it is a configuration change (a second container
consuming only `live-chunk-jobs`), not a redesign. Note that a second process costs a second copy of the
model weights, which §3.3 shows is not free on a small card.

### 6.4 Cross-chunk speaker identity

pyannote clusters over whatever audio it is given, so `SPEAKER_00` in chunk 3 has no relationship to
`SPEAKER_00` in chunk 4. Per-chunk labels shown raw would reshuffle every 30 s - worse than no
attribution, because it reads as though it means something.

The stitcher is a new **pure** service, `LiveSpeakerStitcher`:

- Keeps a running set of session centroids, one per live label, as a mean of the ECAPA vectors seen for
  that label (stored on `Speaker.Embedding`, which already exists and is `vector(192)`).
- For each chunk-local label, ranks its embedding against the session centroids by cosine distance.
  Accept the best match when it clears `Live:StitchThreshold` **and** beats the runner-up by
  `Live:StitchMargin`; otherwise mint a new live label.
- Independently ranks against the platform voiceprint directory via the existing `ISpeakerIdentifier`,
  reusing `IdentificationRules.Decide`, so a known colleague gets a real name rather than
  `SPEAKER_01`.

Two measured caveats the design must tolerate:

- ECAPA on 15-30 s of one voice is noisy. This is the real floor under chunk length, not compute.
- pyannote found **three** speakers in a 20 s clip containing two. Short-window clustering
  over-segments, so the stitcher must merge chunk-local labels onto one session label, never assume a
  bijection.

**A live match must never enrol.** The existing rule that automatic identification does not write a
`VoiceSample` holds here without exception: provisional, short-window matches are exactly the input that
should not be allowed to pollute a platform-wide centroid (see the identification notes in `CLAUDE.md`).

### 6.5 SignalR

One new event on the existing `TranscriptionHub`, scoped to the per-user group as everything else is:

```
LiveTranscriptAppended { recordingId, transcriptionId, sequence, segments[], speakers{} }
```

`RecordingStatusChanged` continues to carry lifecycle transitions, including the new `Live` status, so
list views need no new subscription.

### 6.6 Chat

No change to `ChatController`. It loads the current transcription's segments ordered by `Ordinal` with
no status filter, so a provisional transcription is picked up as-is. Two adjustments elsewhere:

- `ChatContextBuilder` marks provisional content in the prompt, so the model knows the meeting is still
  running and does not describe a partial discussion as concluded.
- Live RAG is off (D5).

---

## 7. Schema changes

All additive. See `docs/Data_Schema.md`, updated in the implementing PR.

### 7.1 New entity `RecordingChunk`

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK -> `Recordings` | cascade delete |
| `Sequence` | int | 0-based; unique with `RecordingId` |
| `BlobKey` | text | `{userId}/{recordingId}/chunks/{sequence:D5}.webm` |
| `StartMs` / `EndMs` | bigint | relative to the recording |
| `SizeBytes` | bigint | counts toward quota until finalise (§12.1) |
| `ReceivedAt` | timestamptz | |
| `TranscribedAt` | timestamptz null | null = enqueued but not yet returned |

Unique index on `(RecordingId, Sequence)` - the idempotency key for retries (§9.2). Rows and blobs are
deleted after a successful concat.

### 7.2 `Transcription.IsProvisional`

`bool`, default `false`. Set on the live pass only. Every consumer that must not act on provisional text
gates on it:

| Consumer | Behaviour when `IsProvisional` |
|---|---|
| `SummarizationWorker`, `ActionsWorker`, `TagsWorker`, `MeetingMinutesWorker` | never enqueued |
| `EmbeddingWorker` | never enqueued (D5) |
| `WebhookEventTypes.RecordingTranscribed` | not published |
| Transcript export endpoints (`transcript.txt/md/rtf/srt`) | 409 with an explanatory message |
| MCP tools, `SearchController` | excluded from results |
| `RecordingsController.Get` | returned as normal, with the flag on `TranscriptionDto` so the UI can label it |

### 7.3 `RecordingStatus.Live = 8`

Append-only, following `Merging = 7`. Never renumber - values persist as ints.

### 7.4 Backup-restore

None of this is destructive and an older backup restores unchanged, so
`MaintenanceController.CurrentFormat` is **not** bumped. A restored backup can contain a `Live`
recording that no client will ever finalise; the reaper in §9.4 collects it on the same timer as any
other abandoned session.

---

## 8. API surface

| Endpoint | Purpose |
|---|---|
| `POST /api/recordings/live` | Begin. Body mirrors the `Upload` form fields (title, source, startedAt, roomId, sectionId) minus the audio. Enforces the same room permission check. Returns the recording id. |
| `PUT /api/recordings/{id}/chunks/{sequence}` | One chunk, multipart. Idempotent on `(id, sequence)`. Rejects when the recording is not `Live` or not the caller's. |
| `POST /api/recordings/{id}/live/finalize` | Stop. Concats, then runs the normal transcription. Reports which sequences are missing if there is a gap (§9.2). |
| `DELETE /api/recordings/{id}/live` | Abandon and discard. |

Quota is charged at `POST /api/recordings/live` against a caller-declared expected duration, and
reconciled at finalise - a live recording cannot know its size in advance, and the existing check in
`Upload` runs against a known `audio.Length` (§12.1).

---

## 9. Edge cases and failure modes

### 9.1 The network drops mid-meeting

Chunks queue client-side in IndexedDB (the `pendingRecording.ts` pattern, extended to a queue keyed by
sequence) and upload when connectivity returns. Recording never blocks on the network.

### 9.2 A chunk is lost

`PUT .../chunks/{sequence}` is idempotent on `(RecordingId, Sequence)`, so a retry is safe. `finalize`
computes the expected sequence set from the highest sequence received and refuses to concat if any are
missing, returning the gap. The client retries the named sequences from its IndexedDB queue, then
finalises again. Only if a chunk is genuinely unrecoverable does the user get a choice: finalise with a
gap, or keep waiting.

### 9.3 The recording is paused

The recorded clock is already pause-aware (`Recorder.tsx` `timing.pause`). Chunk boundaries are driven
by that clock, so a pause simply stops producing chunks. `StartMs`/`EndMs` stay in recorded time, which
is what `Segment` offsets already mean.

### 9.4 The client vanishes (lid closed, tab killed, browser crash)

A new hosted service reaps `Live` recordings whose newest chunk is older than `Live:AbandonAfterMinutes`
(default 30): finalise from whatever arrived, or delete if no chunk ever did. **This is a strict
improvement over today**, where the same event loses the entire meeting.

### 9.5 Two devices, one account

`POST /api/recordings/live` takes a client-generated session id and the chunk endpoint requires it. A
second device gets 409 rather than interleaving its chunks into the first device's recording. The
desktop single-instance lock covers one shell, not one account, so this cannot be left to the client.

### 9.6 The GPU cannot keep up

If `TranscribedAt` lag exceeds `Live:MaxLagSeconds`, the API stops enqueueing live jobs for that
recording, emits a `LiveTranscriptDegraded` event, and the UI says the live transcript has paused and
will be complete after the meeting. **Capture is unaffected** - chunks keep uploading, and the final
transcript is unharmed. This is the designed response to §12.4 hardware, and to unexpected load.

### 9.7 Silence

A chunk with no speech returns no segments. That is normal mid-meeting and must not be treated as the
failure `WorkerCallbackController.Result` reports for a whole recording with no speech.

---

## 10. UX

The live transcript renders in the existing in-meeting surface rather than a new one: the notes panel
(`useLiveNotes` / `useNotesPopout` / `notesChannel.ts`) gains a **Transcript** tab beside Notes, and it
travels into the pop-out window with the notes. Chat is reachable from the same place and is scoped to
the running recording.

- Provisional text is visually distinguished (the tail of the transcript is not yet stable, and speaker
  labels can be revised by the stitcher).
- A speaker rename during the meeting applies to the session label and is preserved onto the final
  transcript by the existing rename-preservation rule.
- A status line shows how far behind the transcript is, so the §9.6 degradation is legible rather than
  mysterious.

Help content (`apps/web/src/content/help/**`) gains an article, since this changes behaviour a user
relies on.

---

## 11. Testing (TDD, per CLAUDE.md)

Failing test first, in every case. New pure modules exist precisely so they can be tested without a
media stream, a GPU, or a browser.

| File | Kind | Covers |
|---|---|---|
| `apps/web/src/lib/liveChunker.test.ts` | vitest, pure | Boundary decisions from (level, elapsed, pause state): cut at the first >= `PauseMs` silence after `MinMs`; force at `MaxMs`; never cut while paused |
| `apps/web/src/lib/liveChunkQueue.test.ts` | vitest, pure | Sequence assignment, retry ordering, gap detection, IndexedDB degradation to memory-only |
| `apps/web/src/lib/liveTranscript.test.ts` | vitest, pure | Applying an append event: ordering by sequence, idempotent re-delivery, speaker relabelling |
| `tests/Diariz.Api.Tests/LiveSpeakerStitcherTests.cs` | xUnit, pure | Cosine matching with hand-built vectors: merge onto an existing label, mint a new one, over-segmentation (3 chunk labels onto 2 session labels), never enrol |
| `tests/Diariz.Api.Tests/LiveRecordingControllerTests.cs` | xUnit, in-memory | Ownership, room permission, idempotent chunk PUT, 409 on a foreign session id, finalize gap reporting |
| `tests/Diariz.Api.Tests/ProvisionalTranscriptionGateTests.cs` | xUnit, in-memory | Every consumer in §7.2 declines provisional input |
| `tests/Diariz.Api.IntegrationTests/LiveChunkFlowTests.cs` | Testcontainers | Real MinIO round-trip, the `(RecordingId, Sequence)` unique constraint, `live-chunk-jobs` wire format, cascade delete |
| `src/Diariz.Worker/tests/test_live_chunk.py` | pytest, models stubbed | Job orchestration, overlap prepend and trim-back, temp cleanup, callback body shape |

The web suite's `act()` guard and pristine-output rule apply as everywhere else. Note that a local
Windows run cannot be trusted to reproduce CI (`vitest.config.ts` reporter caveat in `CLAUDE.md`); run
the suite on Linux before claiming it passes.

---

## 12. Second-order consequences

### 12.1 Storage quota

Chunks plus a concatenated blob charge the owner twice until cleanup. Today `SizeBytes` is set once from
a known `audio.Length` before anything is stored. Live recordings must instead charge incrementally as
chunks arrive and reconcile at finalise, and the reaper (§9.4) must release the charge for an abandoned
session.

### 12.2 Retention

`AudioRetention` operates on `Recording.CreatedAt`. A `Live` recording is not a candidate until
finalised; the reaper handles the rest.

### 12.3 Queue contention

Downgraded from a headline risk by §3. A 60-minute meeting occupies the serial worker ~75 s, so a live
chunk waits a minute or two at worst. The priority read (§6.3) is specified; a second worker process is
not.

### 12.4 Hardware floor

**New constraint.** §3.3 shows an 8 GB GPU cannot run this pipeline live. Live transcription must
therefore be a capability the server determines at startup - from available VRAM and a timed probe -
and advertises through `ConfigController`, rather than a flag an operator sets by hand and discovers is
wrong mid-meeting. When it is off, the recorder falls back to today's behaviour exactly.

### 12.5 Privacy

Audio leaves the device continuously rather than in one deliberate act at Stop. Because §3 removed any
need for a hosted transcription endpoint, it still never leaves the deployment - so this is a change in
timing, not in trust boundary. Worth stating plainly in the help article all the same.

---

## 13. Non-goals

- **Sub-five-second latency.** That rules out chunked file transcription entirely and points at a
  streaming ASR service - a different project. Lag here is dominated by the chunk length, floored by
  speaker-clustering quality (§6.4), not by the GPU.
- **Live summary, minutes, actions or tags.** Provisional text, repeated LLM spend, and a moving target.
  All of it runs once, at the end, unchanged.
- **Live translation.**
- **Mobile.** M4's mobile app is not built; nothing here assumes it.
- **Carrying live segments forward into the final transcript.** The final pass supersedes them (D3).
  Reconciliation is a later option, not part of this.
- **Multi-device capture into one recording.** Explicitly refused (§9.5).

---

## 14. Acceptance criteria

1. Pressing Record creates a `Recording` server-side with `Status = Live`, visible in the list, before
   any audio is captured.
2. Audio arrives continuously; killing the tab mid-meeting loses at most the last chunk, and the
   recording is recoverable by the reaper.
3. A transcript appears in the notes panel within `chunk length + ~5 s` of speech and continues to
   update, with a visible lag indicator.
4. Chat answers questions about what has been said so far, while the meeting runs.
5. Speaker labels remain stable for the duration of the meeting; an enrolled colleague is named.
6. On Stop, the canonical blob is byte-identical to what today's path would have produced (D1 option A),
   and the final transcript is produced by the existing unchanged pipeline.
7. No summary, minutes, actions, tags, embeddings, webhook or export is ever produced from provisional
   text.
8. With live transcription unavailable (§12.4) or degraded (§9.6), recording and the final transcript
   are indistinguishable from today.
9. `dotnet test`, `npm test` (on Linux) and `python -m pytest` all pass with no new warnings.

---

## 15. Implementation plan

Five PRs. Each is independently shippable and independently revertible.

| # | PR | Risk | Ships |
|---|---|---|---|
| S0 | **Framing spike** (§5.1) | - | A findings section appended to this doc. No product code. |
| 1 | **Streaming capture** - chunk upload, concat at finalise, reaper, quota reconciliation. No live transcript. | Low | Durability during the meeting, no upload cliff, no client memory growth |
| 2 | **Live transcript, session-local labels** - `live-chunk-jobs`, worker handler, provisional transcription, SignalR, notes-panel tab | Medium | The transcript during the meeting |
| 3 | **Cross-chunk speaker identity** - `LiveSpeakerStitcher`, directory matching | Medium | Stable speakers, real names |
| 4 | **Chat over the live transcript** - provisional marking in the context builder, in-meeting entry point | Low | The stated goal |

PR 1 is worth shipping even if 2-4 never happen. PR 4 is small because §6.6 requires almost nothing.

### Release checklist impact

**This doc is a docs-only change**: no version bump, no `RECENT` entry, no `CAPABILITIES` or README
edit. It does update `Overall_Synopsis_of_Platform.md` for the throughput correction in §4, which is a
factual fix to an existing document rather than a new claim about the product.

Each implementation PR does the full checklist in `CLAUDE.md`: version + seven mirrors, a `RECENT[0]`
entry in `apps/web/src/lib/releaseNotes/current.ts`, and - because this adds a user-facing capability - the
README Features row, the `docs/features.md` bullet and the About-box `CAPABILITIES` row (in
`apps/web/src/lib/appInfo.ts`) in lockstep. PR 1 and PR 2 both touch
`Overall_Synopsis_of_Platform.md` (new stream, new contract, new hosted service) and `Data_Schema.md`
(new entity, new column, new enum value).

---

## 16. Appendix - measurement method

The harness is checked in at **`tools/transcription-bench/`** with its own README; what follows is
the method and the caveats that belong with the numbers in §3. Reproducible without any production
data.

**Test audio.** A synthetic two-speaker "meeting" generated with Windows TTS (two English voices,
varied speaking rate, invented content) by `make-audio.ps1`, rendered to 16 kHz mono 16-bit WAV, then
sliced by `slice.py` with Python's stdlib `wave` module into a contiguous ladder of 5, 10, 15, 20, 30,
45, 60, 90, 120, 180 and 240 s clips from a common 2 s offset. Only the length varies across the ladder.
Benchmark material is synthesised rather than sampled precisely so it can never be production audio.

**Two harnesses:**

- *In-container, per-stage* (`chunk_floor.py`, driven by `run-local.ps1`). Replicates
  `pipeline.transcribe()` stage by stage with a timer around each call, run inside the worker image with
  the models warm. Must apply `rocm_env.clean_gfx_override()` and
  `torch_compat.restore_legacy_torch_load()` before importing `pipeline` - `worker.py` does this and a
  script that imports `pipeline` directly does not, so the pyannote VAD checkpoint fails to load on
  torch >= 2.6 without it. Touches no queue and writes nothing back.
- *Via the REST API, end-to-end* (`api_floor.py`). Uploads each clip, polls until transcribed, and reads
  `RecordingDetailDto.Current.ProcessingMs` (note: the field is `current`, not `transcription`). Needs
  only a `dz_api_` token, so it works against any instance without shell access. It reports queue wait
  separately from processing time so contention is visible rather than silently inflating results.

**Supporting tools.** `speakers.py` confirms diarization actually ran on both boxes rather than being
silently skipped - without which a cross-box timing comparison is meaningless, since diarization is
75-80% of the cost. `validate.py` produced §3.2 and is the only tool in the set that reads production
data; it emits counts, ratios and percentiles only. `cleanup.py` removes whatever an interrupted run
left behind.

**Hygiene.** Benchmark recordings are titled `floor-bench <n>s`. Cleanup matches on `Recording.Title`,
never `Name` - the summariser overwrites `Name` and would defeat a name-based filter, while `Title` is
the auto descriptor and stays stable. All 33 benchmark recordings created for §3.1 were deleted
afterwards and the instance was verified back to its prior count.

**Caveats on these numbers.**

- The 5090 box was effectively idle (queue waits 0.3-1.7 s). Behaviour under concurrent load is
  unmeasured, which is why §9.6 exists.
- Synthetic speech has clean turn-taking and no overlap. §3.2 exists because of that, and shows real
  audio is cheaper, not dearer.
- Each API-route upload also triggers the owner's summary/actions/tags jobs, so a re-run spends LLM
  calls. Use the in-container harness where that matters.
- **A straight-line fit is not always valid.** Both harnesses fit `total = floor + slope * seconds`,
  which holds only where the curve has no regime change. The 4070 ladder steps at 20 s, and fitting
  one line across that step returns a *negative* intercept - an artefact, not a floor. Both tools now
  say so when the intercept comes out below zero. Prefer a measured row over the fit generally: the
  fit is least accurate mid-ladder, which is exactly where the live chunk sizes are (it predicts 2.1 s
  for a 30 s clip on the 5090 against 2.67 s measured).
