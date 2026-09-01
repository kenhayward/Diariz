# Streaming Capture (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A recording exists server-side from the moment Record is pressed and grows as the meeting runs, instead of arriving in one multipart POST at Stop. No live transcript in this phase - the canonical blob is assembled at Stop and the existing transcription pipeline runs on it, unchanged.

**Architecture:** One additive entity (`RecordingChunk`) and one appended `RecordingStatus`. Three new endpoints begin, extend and finalise a live recording; chunks land in MinIO as they arrive. Finalise reuses the existing `audio-merge-jobs` stream and the Python worker's `handle_merge`, distinguished from a recordings-merge by a new `Kind` field - which the worker uses to byte-join the fragments before handing ffmpeg a single input, because the existing concat command cannot open a headerless fragment (spec §5.1 findings). A background reaper finalises sessions whose client vanished. The web recorder gains three pure modules (a boundary chooser, a durable upload queue, and a framing probe) and keeps its existing single `MediaRecorder`.

**Tech Stack:** ASP.NET Core 10 + EF Core (Npgsql), xUnit + Testcontainers, Python 3.10 + pytest, React 19 + TypeScript + Vite, vitest + @testing-library/react, i18next.

**Spec:** [docs/Streaming_Capture_and_Live_Transcript.md](../../Streaming_Capture_and_Live_Transcript.md) - this plan implements **PR 1 of §15** plus the **S0 spike of §5.1**.

**Out of scope, deliberately.** PR 2-4 (live transcript, cross-chunk speaker identity, chat over the live transcript) get their own plans, written once PR 1 has landed and its shape is real rather than predicted. §15 makes PR 1 independently shippable, so nothing here waits on them.

**Task 1 is complete.** The S0 spike ran on 2026-08-30 and adopted option A, with two amendments already folded into Tasks 5 and 11. Its findings are in spec §5.1. Start at Task 2.

## Global Constraints

- **TDD is mandatory.** No production code without a failing test that preceded it. Every guard below must be **mutation-verified**: after it passes, introduce the exact regression it exists to catch, watch it fail, revert. A guard never seen to fail is not a guard.
- **Task 1 is a spike and is the one exception.** CLAUDE.md allows throwaway spikes without a preceding test, but they need a human's sign-off. Get it, and do not let spike code become production code - Task 2 starts from a clean tree.
- **Never `git add -A`.** Stage explicit paths only.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only, in UI strings, locale catalogues, help articles and release notes. `apps/web/src/lib/noFancyDashes.test.ts` enforces it. Code, comments and internal docs are exempt.
- **Never put production data anywhere in the repo.** Invent fixture names (`Ada`, `Grace`, `Alice`, `Bob`, `Sam`).
- **`main` is branch protected.** Work lands via a PR. Never commit or push to `main`, never merge locally.
- **Branch:** `claude/streaming-capture-phase-1`, off `origin/main` at `75fe5bc9`.
- **Target version:** `0.262.0` -> **`0.263.0`** (functional enhancement, minor bump). Applied once, in Task 12.
- **Do not bump `MaintenanceController.CurrentFormat`.** Every change here is additive, so an older backup restores unchanged (spec §7.5). A `Live` recording inside a restored backup is collected by the Task 6 reaper like any other abandoned session.
- **The web suite cannot be trusted locally on Windows.** `vitest.config.ts` pins `reporters: ["default"]`; do not remove it. Before claiming the web suite passes, run it on Linux (mount the repo read-only into `node:24`, `tar` everything except `node_modules`, `npm ci`, `npx vitest run`).
- **`dotnet test --filter "Name=X"` does not work in this repo** despite what CLAUDE.md says. Always use `FullyQualifiedName~X`.
- **Split queries are the app-wide default.** Just write the `Include`s; do not add `.AsSplitQuery()`. The one caveat that applies here: a `Skip`/`Take` over a query that also `Include`s a collection needs a deterministic `OrderBy`. Task 6's reaper query projects with `Select`, so it does not hit this.

**Commands:**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LiveRecording"
```

```bash
cd apps/web && npx vitest run src/lib/liveChunker.test.ts
```

```bash
cd src/Diariz.Worker && python -m pytest tests/test_worker.py -k merge
```

---

### Task 1: S0 spike - does fragment concatenation survive a round trip?

> **DONE 2026-08-30.** Verdict: **option A adopted**, with two amendments - Task 5 gains a worker-side
> byte-join (the existing concat command cannot open raw fragments), and Task 11 gains a runtime framing
> probe (Firefox and Safari could not be tested). Full findings in spec §5.1.

**Spike, not TDD.** Produces no production code and no test. It produces a findings section appended to the spec, and a decision. **Get a human's sign-off before starting and before recording the verdict.**

The question, from spec §5.1: `MediaRecorder.start(timeslice)` emits fragments where only the first carries the EBML header and Opus codec-private data. Two things must hold for **option A** (which keeps the canonical audio byte-identical to today):

1. Byte-concatenating every fragment in order produces a file ffmpeg decodes identically to the single-blob recording the current recorder makes.
2. `fragment[0] + fragment[k..n]` decodes correctly, so a later window can be transcribed without the whole prefix. (Not needed by PR 1, but if it fails, PR 2 has to change shape and it is far cheaper to know now.)

**Files:**
- Create: `tools/streaming-spike/` (scratch; **deleted in Task 2**, not shipped)

- [x] **Step 1: Build the capture page**

A single self-contained HTML page that records ~60 s from the microphone twice over: once with `recorder.start()` (today's behaviour, one blob) and once with `recorder.start(20000)`, keeping every fragment separately. Download all of it.

- [x] **Step 2: Compare, per browser**

For each of Chrome, Firefox and the Electron shell (`cd apps/desktop && npm run dev`), and Safari if a Mac is to hand:

```bash
ffprobe -v error -show_entries format=duration,size -of default=noprint_wrappers=1 single.webm
```

```bash
cat fragment-*.webm > concatenated.webm && ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1 concatenated.webm
```

Then decode both to PCM and compare, which is the test that actually matters - a container that probes fine can still decode short:

```bash
ffmpeg -v error -i concatenated.webm -f s16le -ar 16000 -ac 1 - | sha256sum
```

Record: duration, decoded sample count, and whether the hashes match. Then repeat for `fragment[0] + fragment[2..n]` (expect a shorter but clean decode, no errors on stderr).

- [x] **Step 3: Record the verdict in the spec**

Append a `### 5.1 findings` subsection to `docs/Streaming_Capture_and_Live_Transcript.md` with a per-browser table and the decision. Then:

- **If option A holds everywhere:** proceed as planned. Chunks are `start(timeslice)` fragments.
- **If any browser fails:** switch to **option B** (stop/restart per chunk, as `dictationEngine.ts` `createServerEngine` already does). Then also add a sentence to spec §7.2 recording that the canonical audio has sub-second scattered gaps, and re-read §5.4 - option B plus audible gaps is the one condition under which the rejected two-recorder design comes back.

Everything downstream of this task is identical either way; only what the browser hands the queue in Task 11 changes.

- [x] **Step 4: Commit the findings, delete the spike**

```bash
git add docs/Streaming_Capture_and_Live_Transcript.md && git commit -m "docs: record the S0 chunk-framing spike findings"
```

---

### Task 2: `RecordingChunk`, `RecordingStatus.Live`, and the migration

**Files:**
- Create: `src/Diariz.Domain/Entities/RecordingChunk.cs`
- Modify: `src/Diariz.Domain/Entities/RecordingStatus.cs`
- Modify: `src/Diariz.Domain/Entities/Recording.cs` (navigation collection)
- Modify: `src/Diariz.Domain/DiarizDbContext.cs` (`OnModelCreating`)
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddRecordingChunks.cs` (+ `.Designer.cs`, generated)
- Modify: `src/Diariz.Domain/Migrations/DiarizDbContextModelSnapshot.cs` (generated)
- Test: `tests/Diariz.Api.IntegrationTests/RecordingChunkSchemaTests.cs`

**Interfaces:**
- Produces: `RecordingChunk`, `RecordingStatus.Live = 8`, `Recording.Chunks`.

- [ ] **Step 1: Write the failing integration test**

The unique constraint and the cascade are the whole point of the entity, and the in-memory provider enforces neither - so this test belongs in the integration project from the start, not as a unit test that would pass for the wrong reason.

```csharp
[Collection("integration")]
public class RecordingChunkSchemaTests
{
    private readonly ContainersFixture _fx;
    public RecordingChunkSchemaTests(ContainersFixture fx) => _fx = fx;

    [Fact]
    public async Task DuplicateSequenceForOneRecording_IsRejected()
    {
        await using var db = _fx.CreateDbContext();
        var rec = await SeedLiveRecordingAsync(db);

        db.RecordingChunks.Add(New(rec.Id, sequence: 0));
        await db.SaveChangesAsync();

        db.RecordingChunks.Add(New(rec.Id, sequence: 0));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SameSequenceOnADifferentRecording_IsAllowed()
    {
        // The constraint is (RecordingId, Sequence), not Sequence - two concurrent live
        // recordings both start at 0.
    }

    [Fact]
    public async Task DeletingTheRecording_CascadesTheChunks()
    {
    }
}
```

- [ ] **Step 2: Watch it fail** (`RecordingChunks` does not exist), then write the entity

Per spec §7.1. `Sequence` is 0-based. `BlobKey` follows `{userId}/{recordingId}/chunks/{sequence:D5}.webm` - zero-padded so an object-store listing sorts correctly, which is what the finalise job relies on if it ever has to reconstruct order without the database.

- [ ] **Step 3: Append `Live = 8` to `RecordingStatus`**

Append only. Never renumber - values persist as ints, and `Merging = 7` is the precedent.

- [ ] **Step 4: Configure and migrate**

Unique index on `(RecordingId, Sequence)`, cascade delete from `Recording`. Nothing here is Postgres-only, so it needs **no** `Database.IsNpgsql()` guard - unlike the pgvector columns beside it.

```bash
dotnet ef migrations add AddRecordingChunks --project src/Diariz.Domain --startup-project src/Diariz.Api
```

- [ ] **Step 5: Green, then mutation-verify**

Drop the `IsUnique()` from the index, watch `DuplicateSequenceForOneRecording_IsRejected` fail, revert. Change the cascade to `DeleteBehavior.Restrict`, watch the cascade test fail, revert.

- [ ] **Step 6: Commit**

---

### Task 3: `POST /api/recordings/live` - begin a session

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Domain/Entities/Recording.cs` (`LiveSessionId`) + a migration - **not** foreseen when this plan was written; the session id of §9.5 has to live somewhere, and Task 2's migration was already committed
- Create: `tests/Diariz.Api.Tests/LiveTestSupport.cs` (shared seeding/wiring for Tasks 3-7)
- Test: `tests/Diariz.Api.Tests/LiveRecordingControllerTests.cs` (new)

**Interfaces:**
- Produces: `BeginLiveRecordingRequest(string? Title, RecordingSource Source, Guid? SectionId, Guid? RoomId, DateTimeOffset? StartedAt, Guid SessionId, long ExpectedDurationMs)` and `LiveRecordingDto(Guid Id, Guid SessionId, RecordingStatus Status)`.

- [ ] **Step 1: Write the failing tests**

Four behaviours, each a separate test. The first three are the ownership and permission rules `Upload` already enforces and which a second entry point must not quietly drop:

- `Begin_CreatesRecordingWithLiveStatus_AndPlacesItInThePersonalRoom`
- `Begin_IntoASharedRoomWithoutCreateRecording_Returns403` (mirrors `Upload`'s check exactly)
- `Begin_WhenQuotaWouldBeExceededByTheEstimate_Returns413`
- `Begin_WithAnImplausibleStartedAt_FallsBackToCreatedAt` (reuses `PlausibleCaptureTime`)

- [ ] **Step 2: Watch them fail, then implement**

Reuse, do not reimplement: `_rooms.PersonalRoomIdAsync`, `_rooms.PermissionsAsync`, `_rooms.PlaceInMainRoomAsync`, `PlausibleCaptureTime`. The recording is created with `Status = Live`, `BlobKey` empty (it has no blob yet) and `SizeBytes = 0`.

`SessionId` is client-generated and stored on the recording. It is what stops two devices interleaving chunks into one recording (spec §9.5), so it is required, not optional.

**Quota** is charged against `ExpectedDurationMs` at a conservative bytes-per-second constant, and reconciled in Task 5. A live recording cannot know its size in advance, and the existing check runs against a known `audio.Length` - so this is a genuinely new code path, not a reuse. Put the estimate in one named constant with a comment saying what bitrate it assumes.

- [ ] **Step 3: Green, mutation-verify the 403**

Change the permission check to `RoomPermission.ViewRecording`, watch `Begin_IntoASharedRoomWithoutCreateRecording_Returns403` fail, revert. This is the guard that matters most: it is the one an attacker would probe.

- [ ] **Step 4: Commit**

---

### Task 4: `PUT /api/recordings/{id}/chunks/{sequence}` - the chunk endpoint

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs`
- Test: `tests/Diariz.Api.Tests/LiveRecordingControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

- `PutChunk_StoresTheBlobAndCreatesTheRow`
- `PutChunk_Twice_IsIdempotent` - the same sequence uploaded again replaces the blob and does **not** create a second row, and does not double-charge quota. This is the retry path from spec §9.2 and it will be exercised by every flaky network in production.
- `PutChunk_ForAnotherUsersRecording_Returns404` (not 403 - do not confirm the id exists)
- `PutChunk_WithAMismatchedSessionId_Returns409`
- `PutChunk_WhenTheRecordingIsNotLive_Returns409`

- [ ] **Step 2: Watch them fail, then implement**

`FakeAudioStorage` from `Diariz.Api.TestSupport` covers the MinIO side; a real round trip is Task 8.

Note the ordering that makes idempotency real: write the blob **first**, then upsert the row. A crash between the two leaves an orphaned blob that finalise ignores and the reaper cleans, which is recoverable; the reverse leaves a row pointing at nothing, which is not.

- [ ] **Step 3: Green, mutation-verify idempotency**

Change the upsert to an unconditional `Add`, watch `PutChunk_Twice_IsIdempotent` fail on the duplicate row, revert.

- [ ] **Step 4: Commit**

---

### Task 5: `POST /api/recordings/{id}/live/finalize` - concatenate and transcribe

This is the task with a cross-boundary contract change. Read spec §6.1 **and the §5.1 findings** first.

> **Amended after the S0 spike.** The original plan said the worker needed no change beyond echoing a
> `Kind` field. That is wrong: `build_concat_command` opens each input independently and dies on the
> second fragment with *"EBML header parsing failed"*, because only fragment 0 carries the header. The
> worker must **byte-join the chunk blobs into one file, then pass that single file** to the existing
> command. Verified in the spike - the output decodes to 12.00 s and `probe_duration_ms` returns a
> proper duration, so nothing downstream changes.

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs`
- Modify: `src/Diariz.Api/Contracts/WorkerContracts.cs` (`AudioMergeJob`, `AudioMergeResult`)
- Modify: `src/Diariz.Api/Controllers/WorkerMergeCallbackController.cs`
- Modify: `src/Diariz.Worker/audio_merge.py` (new `join_then_concat`)
- Modify: `src/Diariz.Worker/worker.py` (`handle_merge` branches on `Kind`, echoes it back)
- Modify: `src/Diariz.Worker/callback.py` (`post_merge_result` carries `Kind`)
- Test: `tests/Diariz.Api.Tests/LiveRecordingControllerTests.cs`, `tests/Diariz.Api.Tests/WorkerMergeCallbackTests.cs`
- Test: `src/Diariz.Worker/tests/test_audio_merge.py`, `src/Diariz.Worker/tests/test_worker.py`

**Interfaces:**
- Changes: `AudioMergeJob` and `AudioMergeResult` each gain a trailing `string Kind = "recordings"`. `"live-chunks"` selects the byte-join path in the worker **and** the new callback branch in the API.
- Produces: `audio_merge.join_then_concat(paths) -> (output_path, duration_ms, size_bytes)` - byte-joins in order to a temp file, then delegates to the existing `concat([joined])`. Pure enough to unit-test the joining separately from ffmpeg.

- [ ] **Step 1: Write the failing API tests**

- `Finalize_WithAContiguousChunkSet_EnqueuesAMergeJobOverTheChunkKeysInOrder`
- `Finalize_WithAGap_Returns409AndNamesTheMissingSequences` - the client retries those from its IndexedDB queue and finalises again (spec §9.2). Naming them is the whole point; a bare 409 leaves the client unable to act.
- `Finalize_WithNoChunks_DeletesTheRecording` - a session where nothing ever arrived is not a recording.
- `Finalize_WhenAlreadyFinalizing_IsIdempotent`

- [ ] **Step 2: Write the failing callback test**

`MergeResult_WithKindLiveChunks_SwapsTheBlob_DeletesChunkRows_AndEnqueuesTranscription` - and, critically, a companion asserting the **existing** behaviour is untouched:

`MergeResult_WithKindRecordings_StillDeletesTheSourceRecordings_AndDoesNotEnqueueTranscription`

That second test is the one that stops this task breaking `RecordingsController.Merge`, which has been in production since long before this feature.

- [ ] **Step 3: Write the failing worker tests**

In `src/Diariz.Worker/tests/test_audio_merge.py`:

- `test_join_then_concat_byte_joins_in_order` - the joined temp file is the inputs concatenated in the
  order given, byte for byte. Assert on bytes, not on a duration: the whole point is that the fragments
  are not individually parseable, so anything that opens them to check has already failed.
- `test_join_then_concat_passes_one_input_to_ffmpeg` - patch `build_concat_command` and assert it was
  called with a **single** path. This is the regression guard for the exact mistake the spike caught, so
  mutation-verify it by passing the raw list through and watching it fail.

In `src/Diariz.Worker/tests/test_worker.py`:

- `test_handle_merge_live_chunks_uses_join_then_concat`
- `test_handle_merge_recordings_still_uses_concat` - the existing path, unchanged.
- `test_handle_merge_echoes_kind` - the worker branches on `Kind` for the join, but does not otherwise
  interpret it; the callback carries it straight back so the API decides what happens next.

- [ ] **Step 4: Watch all three fail, then implement**

The API side sorts chunks by `Sequence`, computes the expected set as `0..max`, and refuses on a gap. On success: `Status = Merging`, enqueue `AudioMergeJob` with `Kind = "live-chunks"` and `DeleteRecordingIds` empty.

The callback's live branch swaps the blob (exactly as the recordings branch does), deletes the `RecordingChunk` rows **and their blobs**, reconciles `SizeBytes` to the real concatenated size, then calls the existing `EnqueueTranscriptionAsync` so the normal pipeline takes over from here with no further changes.

- [ ] **Step 5: Green, mutation-verify the branch**

Make the live branch fall through to the recordings branch, watch `MergeResult_WithKindRecordings_...` still pass but `MergeResult_WithKindLiveChunks_...` fail, revert. Then delete the gap check and watch `Finalize_WithAGap_...` fail.

- [ ] **Step 6: Commit**

---

### Task 6: The abandoned-session reaper

A `Live` recording whose client vanished sits open forever otherwise. Note what this buys beyond tidiness: today a closed lid loses the whole meeting, and after this it does not (spec §9.4).

**Files:**
- Create: `src/Diariz.Api/Services/LiveRecordingReaper.cs`
- Modify: `src/Diariz.Api/Program.cs` (`AddHostedService`)
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs` (`Live:AbandonAfterMinutes`, default 30)
- Test: `tests/Diariz.Api.Tests/LiveRecordingReaperTests.cs`

- [ ] **Step 1: Write the failing tests against the pure part**

Follow `AudioRetention.cs`: a pure decision helper plus a sweep plus the `BackgroundService`. The pure part is what gets the exhaustive tests.

```csharp
[Theory]
// status,          minutes since newest chunk, expected
[InlineData(RecordingStatus.Live,       31, true)]
[InlineData(RecordingStatus.Live,       29, false)]
[InlineData(RecordingStatus.Merging,    31, false)]  // finalise already in flight
[InlineData(RecordingStatus.Transcribed,31, false)]
public void IsAbandoned_Cases(RecordingStatus status, int minutes, bool expected) { }
```

Plus a sweep test: an abandoned session **with** chunks is finalised (not deleted - the audio that arrived is the user's), one **without** any chunk is deleted, and its quota charge is released either way.

- [ ] **Step 2: Watch them fail, then implement**

Reuse `AudioRetentionSchedule.NextRun`'s shape but run on an interval rather than a daily time - abandonment is not a nightly concern. A per-item failure is logged and skipped without aborting the sweep, exactly as `AudioRetentionSweep` does.

- [ ] **Step 3: Green, mutation-verify**

Change `>` to `>=` in the threshold comparison and watch the 29-minute case fail. Then remove the `Merging` exclusion and watch that case fail - that one guards against the reaper racing an in-flight finalise, which is the failure mode that would actually corrupt a recording.

- [ ] **Step 4: Commit**

---

### Task 7: Quota reconciliation

Split out from Tasks 3-5 so the whole lifecycle is tested in one place rather than three.

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs`
- Test: `tests/Diariz.Api.Tests/LiveRecordingQuotaTests.cs`

- [ ] **Step 1: Write the failing tests**

- `Begin_ChargesTheEstimate_AgainstTheOwnersQuota`
- `Chunks_AccumulateTowardTheCharge_NotOnTopOfTheEstimate`
- `Finalize_ReconcilesToTheRealConcatenatedSize`
- `Reap_ReleasesTheCharge`
- `AnInProgressLiveRecording_CountsTowardTheNextUploadsQuotaCheck` - otherwise a user can start ten live sessions and blow past their quota, because the existing check in `Upload` sums `Recordings.SizeBytes` and a live recording that has not reconciled yet would contribute nothing.

That last one is the reason this task exists as its own step.

- [ ] **Step 2: Watch them fail, implement, mutation-verify, commit**

---

### Task 8: Integration tests against real Postgres, MinIO and Redis

**Files:**
- Create: `tests/Diariz.Api.IntegrationTests/LiveChunkFlowTests.cs`

- [ ] **Step 1: Write the tests**

The in-memory provider and the fakes cannot see any of this:

- A full begin -> 3 chunks -> finalise cycle against real MinIO, asserting the three blobs exist at their expected keys and the enqueued job names them in sequence order.
- The `live-chunk` merge job's **wire format** on the real Redis stream - the payload is PascalCase JSON produced by .NET and consumed by Python, and a casing regression here is invisible to every C# test.
- Deleting the recording cascades the chunk rows in real Postgres.

- [ ] **Step 2: Green, commit**

---

### Task 9: `apps/web/src/lib/liveChunker.ts` - when to cut

Pure, so it is testable without a media stream. This is the module that decides chunk boundaries; the actual recording stays in `Recorder.tsx`.

**Files:**
- Create: `apps/web/src/lib/liveChunker.ts`, `apps/web/src/lib/liveChunker.test.ts`

**Interfaces:**
- Produces: `shouldCut(state: ChunkerState, now: ChunkerInput): boolean` and `nextState(...)`, over `{ elapsedMs, silentMs, paused }`.

- [ ] **Step 1: Write the failing tests**

Per spec §5.1: cut at the first silence of `PauseMs` (700) or more once `MinMs` (20 000) has elapsed; force a cut at `MaxMs` (45 000) regardless; never cut while paused.

```ts
it("cuts at the first sustained pause after the minimum", () => {});
it("does not cut on a pause before the minimum", () => {});
it("forces a cut at the maximum even mid-sentence", () => {});
it("never cuts while paused, however long the silence", () => {});
it("measures elapsed on the recorded clock, so a pause does not advance it", () => {});
```

That last one matters: the recorded clock is pause-aware (`Recorder.tsx` `timing.pause`), and a chunker on wall-clock time would emit empty chunks through a long pause.

- [ ] **Step 2: Watch them fail, implement, mutation-verify, commit**

Reuse `SILENCE_LEVEL` / `nextSilenceMs` from `audioLevel.ts` rather than inventing a second silence definition - `dictationEngine.ts` and `silenceWatcher.ts` already share those, and a third threshold would drift.

---

### Task 10: `apps/web/src/lib/liveChunkQueue.ts` - durable upload queue

**Files:**
- Create: `apps/web/src/lib/liveChunkQueue.ts`, `apps/web/src/lib/liveChunkQueue.test.ts`

- [ ] **Step 1: Write the failing tests**

- Sequences are assigned monotonically and never reused.
- A failed upload is retried, and retries do not reorder later chunks past earlier ones.
- The queue survives a reload: entries are written to IndexedDB **before** the upload is attempted, and cleared on 2xx.
- With IndexedDB unavailable (jsdom, private browsing), everything degrades to memory-only and **recording still works** - follow `pendingRecording.ts`, which already establishes that "persistence is strictly best-effort and never blocks recording".
- `missingSequences(received, highest)` returns the gap set finalise will complain about.

- [ ] **Step 2: Watch them fail, implement, mutation-verify, commit**

---

### Task 11: Wire the recorder

**Files:**
- Modify: `apps/web/src/components/Recorder.tsx`
- Modify: `apps/web/src/lib/api.ts`
- Modify: `apps/web/src/locales/*/*.json`
- Test: `apps/web/src/components/Recorder.test.tsx`

- [ ] **Step 1: Write the failing component tests**

- Starting a recording calls `beginLive` before any audio is captured.
- Chunks are handed to the queue as the chunker fires.
- Stop calls `finalize`, and a gap response triggers a retry of the named sequences rather than surfacing an error.
- **A `beginLive` failure falls back to today's behaviour** - buffer locally and upload at Stop. The server being briefly unreachable must never cost the user their meeting.

That fallback is the single most important test in this task, and the one most likely to be skipped because it is the unhappy path.

- [ ] **Step 2: Add the framing probe (required by the S0 findings)**

S0 verified option A on Chromium 148 and Electron 43 only. Firefox and Safari were unavailable, and
neither shares the tested muxer, so the recorder **must not assume A holds**. Write the failing test
first, in a new pure module `apps/web/src/lib/chunkFraming.ts`:

- `probeFraming(recordFn, decodeFn)` records two short fragments, byte-joins them, attempts a decode,
  and resolves `"fragments"` or `"restart"`.
- A decode failure resolves `"restart"`, not a rejection - an unknown browser degrades to the option B
  path, it does not lose the recording.
- The result is cached per session; the probe costs about a second and must not run per chunk.

Mutation-verify by making the decode always throw and asserting the recorder still records, via option B.

- [ ] **Step 3: Watch the component tests fail, then implement**

What the browser hands the queue is whatever the probe decided: `start(timeslice)` fragments, or a
stop/restart cycle per chunk (option B, the `createServerEngine` pattern in `dictationEngine.ts`).
Nothing else in this plan changes between them - which is what makes the probe cheap to add.

Keep `chunksRef` for the fallback path only. Once streaming is working it no longer accumulates the whole meeting, which is the ~170 MB-per-three-hours problem from spec §2.

- [ ] **Step 4: Green, run the suite on Linux, commit**

---

### Task 12: Docs and the release checklist

Per CLAUDE.md. This is a user-facing feature, so items 1-7 all apply.

- [ ] **Step 1: Version, `0.262.0` -> `0.263.0`**

`version.json` plus its seven mirrors: `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`, and the three `package-lock.json` files (web, desktop, n8n - **two `version` fields in each**). Edit the lock files by hand; do not regenerate.

- [ ] **Step 2: `RECENT[0]` in `apps/web/src/lib/releaseNotes/current.ts`**

Version, date, `pr`, headline, a PR-level prose summary, and `added`/`changed` bullets. The `pr` number is the issue-free case, so it is the PR's own number - confirm it rather than assuming. `current.ts` is the only release-notes file to touch: since 0.260.0 the history is split, and `lib/releaseNotes/archive.ts` must not be imported by anything but the drill-down page.

- [ ] **Step 3: The three feature surfaces, in lockstep**

The README Features table row, the `docs/features.md` prose bullet, and the About-box `CAPABILITIES` row. Never one without the others.

- [ ] **Step 4: `docs/Overall_Synopsis_of_Platform.md`**

Three things changed there: the new live endpoints, the `Kind` field on the `audio-merge-jobs` cross-boundary contract (both sides), and the new hosted service - which takes the `AddHostedService` count from 15 to 16.

- [ ] **Step 5: `docs/Data_Schema.md`**

`RecordingChunk` with every column, the unique index and the cascade; `RecordingStatus.Live = 8`; the `{userId}/{recordingId}/chunks/{sequence:D5}.webm` MinIO key layout; and the migration-history row.

- [ ] **Step 6: A help article**

`apps/web/src/content/help/**`, ASCII only, with `title` / `summary` / `group` / `order` front matter. This changes behaviour a user relies on - what happens if their laptop sleeps mid-meeting is now a different answer. Keep the `summary` to two or three sentences; it is what the contextual `?` popover shows.

- [ ] **Step 7: Commit**

---

### Task 13: Live verification and the pull request

- [ ] **Step 1: Run the whole suite**

```bash
dotnet test
```

Then the web suite **on Linux**, and `cd src/Diariz.Worker && python -m pytest`. A passing run has no errors and no warnings.

- [ ] **Step 2: Verify against a real stack**

`cd deploy && docker compose up --build`, then record a short meeting in the browser and watch: the recording appears in the list at `Live` before you stop; chunks arrive (`docker compose logs api`); Stop produces one blob and a normal transcript. Then the failure paths, which is where this feature earns its keep: kill the tab mid-recording and confirm the reaper finalises it, and take the network down for a chunk and confirm the retry closes the gap.

- [ ] **Step 3: Open the PR**

State the deployment surface: **server redeploy, no desktop release** - `apps/desktop` is untouched, and the shell loads the web app from the server origin. Note that the worker image must be rebuilt for the `Kind` echo.

## Self-review

- Every task has a failing test written and seen to fail before its implementation, except Task 1, which is a signed-off spike.
- Every guard has been mutation-verified.
- The existing `RecordingsController.Merge` path is covered by its own assertion in Task 5, so the shared callback cannot regress silently.
- The `Upload` path is untouched, and the recorder falls back to it whenever `beginLive` fails.
- No `.AsSplitQuery()` was added.
- No production data appears in any fixture.
- `MaintenanceController.CurrentFormat` is unchanged, deliberately.
- PR 2-4 remain unplanned, deliberately - their shape depends on Task 1 and on what PR 1 actually looks like when it lands.
