# Live Transcript (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The transcript appears while the meeting is still running, and chat can answer questions about what has already been said. This is the thing the whole spec was written for.

**Architecture:** Each chunk that arrives is also queued on a new `live-chunk-jobs` stream. The Python worker transcribes it - with the previous chunk's tail prepended so the seam is clean - and calls back. Segments land on a **provisional** `Transcription` version that the final pass supersedes automatically, because `RecordingsController.Get` already returns only the highest version. The open page is updated over SignalR. Chat pre-loads the provisional transcript; nothing downstream of it (summary, minutes, actions, tags, embeddings, webhooks, exports) ever runs on it.

**Tech Stack:** ASP.NET Core 10 + EF Core (Npgsql), xUnit + Testcontainers, Python 3.10 + pytest, React 19 + TypeScript + Vite, vitest + @testing-library/react, i18next, SignalR.

**Spec:** [docs/Streaming_Capture_and_Live_Transcript.md](../../Streaming_Capture_and_Live_Transcript.md) - this plan implements **PR 2 of §15**.

**Read [the phase 3 plan](2026-09-01-live-speaker-identity-phase-3.md) before starting.** The two were written together because they are one user-visible feature split across two PRs, and the split only works if this one holds a specific line: **phase 2 does not show speaker labels at all.** Per-chunk diarization labels are meaningless across chunks - `SPEAKER_00` in chunk 3 has no relationship to `SPEAKER_00` in chunk 4 (spec §6.4) - so showing them raw would have speakers reshuffling every 30 seconds. That reads as though it means something, which is worse than saying nothing. Phase 3 makes them meaningful and turns them on.

## Global Constraints

- **TDD is mandatory.** No production code without a failing test that preceded it. Every guard must be **mutation-verified**: after it passes, introduce the exact regression it exists to catch, watch it fail, revert. A guard never seen to fail is not a guard - phase 1 found two guards that no test could actually reach, and one mutation *the plan itself proposed* that does not discriminate.
- **Never `git add -A`.** Stage explicit paths only.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only. `apps/web/src/lib/noFancyDashes.test.ts` enforces it. Code, comments and internal docs are exempt.
- **Never put production data anywhere in the repo.** Invent fixture names (`Ada`, `Grace`, `Alice`, `Bob`, `Sam`).
- **`main` is branch protected.** Work lands via a PR. Never commit or push to `main`, never merge locally.
- **Branch:** create `claude/live-transcript-phase-2` off `origin/main`.
- **Target version:** whatever `version.json` holds **+ one minor** (0.263.0 -> 0.264.0 if nothing else has shipped). Confirm rather than assuming - three releases landed between phase 1's plan and its merge.
- **The `pr:` field is a guess until the PR exists.** Phase 1 predicted 684 and got 687. Write it, open the PR, then correct it.
- **The release-notes file is `apps/web/src/lib/releaseNotes/current.ts` (`RECENT`), not `lib/releases.ts`.** The About-box `CAPABILITIES` is in `lib/appInfo.ts`.
- **Do not bump `MaintenanceController.CurrentFormat`.** `IsProvisional` is an additive column with a default; an older backup restores and migrates up cleanly with nothing provisional, which is the correct starting state.
- **The web suite cannot be trusted locally on Windows.** Before claiming it passes, run it on Linux: `docker run --rm -v <repo>:/src:ro node:24 bash -c 'mkdir -p /work && cd /work && tar -C /src -cf - --exclude=node_modules --exclude=.git --exclude=bin --exclude=obj --exclude=omi . | tar -xf - && cd apps/web && npm ci && npx vitest run'`. Copy the **whole tree**, not just `apps/web` - `vitest.config.ts` reads the repo-root `version.json` and the mirror/dash tests read files outside it.
- **`dotnet test --filter "Name=X"` does not work in this repo.** Use `FullyQualifiedName~X`.
- **Expect one intermittent test failure on the first run after a rebuild**, on both the .NET and web suites, that never reproduces. Re-run before investigating; it looks like a build/test-host race on Windows.

**Commands:**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LiveChunkCallback"
```

```bash
cd src/Diariz.Worker && python -m pytest tests/test_worker.py -k live
```

```bash
cd apps/web && npx vitest run src/lib/liveTranscript.test.ts
```

---

### Task 1: `Transcription.IsProvisional` and the consumers that must ignore it

Do this **first**, before anything can create a provisional transcription. The gate is the thing that
keeps a half-finished transcript out of summaries, exports, search and webhooks; adding it after the
producer would leave a window where all of that fires on partial text.

**Files:**
- Modify: `src/Diariz.Domain/Entities/Transcription.cs`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddTranscriptionIsProvisional.cs` (+ generated)
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`TranscriptionDto`)
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (export endpoints)
- Modify: `src/Diariz.Api/Controllers/SearchController.cs`, `src/Diariz.Api/Services/McpResourceService.cs`
- Test: `tests/Diariz.Api.Tests/ProvisionalTranscriptionGateTests.cs` (new)

- [ ] **Step 1: Write the failing tests - one per consumer in spec §7.2**

One test per row of that table, each asserting the consumer declines provisional input:

```csharp
[Fact] public async Task Summarize_OnAProvisionalTranscription_IsNotEnqueued() { }
[Fact] public async Task ActionsTagsAndMinutes_AreNotEnqueuedForProvisionalText() { }
[Fact] public async Task Embedding_IsNotEnqueuedForProvisionalText() { }
[Fact] public async Task RecordingTranscribedWebhook_IsNotPublishedForProvisionalText() { }
[Fact] public async Task TranscriptExport_OnAProvisionalTranscription_Returns409() { }
[Fact] public async Task Search_ExcludesProvisionalSegments() { }
[Fact] public async Task Get_ReturnsTheProvisionalTranscription_WithTheFlagSet() { }
```

That last one is the odd one out and belongs here: the detail endpoint **does** return it, flagged, because the UI has to render it. Everything else refuses.

- [ ] **Step 2: Watch them fail, then add the column and the gates**

`bool IsProvisional`, default `false`. Provider-agnostic, so no `Database.IsNpgsql()` guard.

- [ ] **Step 3: Mutation-verify every gate**

Remove each gate in turn and watch exactly its own test fail. Seven gates, seven mutations. This is the most valuable mutation pass in the plan: each gate is a one-line condition that is trivially easy to write inverted, and a wrong one leaks half a meeting into a summary, an export or a webhook.

- [ ] **Step 4: Commit**

---

### Task 2: The `live-chunk-jobs` stream and its job payload

**Files:**
- Modify: `src/Diariz.Api/Contracts/WorkerContracts.cs` (`LiveChunkJob`, `LiveChunkResult`)
- Modify: `src/Diariz.Api/Services/JobQueue.cs` (`IJobQueue.EnqueueLiveChunkAsync`)
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs` (`JobQueueOptions.LiveChunkStreamKey`)
- Modify: `src/Diariz.Worker/config.py`
- Test: `tests/Diariz.Api.IntegrationTests/LiveChunkQueueIntegrationTests.cs` (new)

**Interfaces:**
- Produces: `LiveChunkJob(Guid RecordingId, Guid TranscriptionId, int Sequence, string BlobKey, string? PrevBlobKey, long OffsetMs, long OverlapMs, string? Language)` and the matching `LiveChunkResult`, both per spec §6.2.

- [ ] **Step 1: Write the failing integration test**

The wire format, on real Redis. This is the test that matters most in this task and cannot be written any other way: the payload is produced by .NET and parsed by Python, and **nothing in the C# tests pairs the key this side writes with the one the worker reads**. Assert the PascalCase key names appear literally in the JSON, not just that a round-trip through `JsonSerializer` works - a round-trip passes even if both sides agree on the wrong name.

- [ ] **Step 2: Watch it fail, implement, mutation-verify by renaming a key, commit**

This takes `IJobQueue` to twelve enqueue methods and the worker to four streams.

---

### Task 3: The worker's live-chunk handler

**Files:**
- Modify: `src/Diariz.Worker/worker.py` (`handle_live_chunk`, and the priority read in `run_loop`)
- Modify: `src/Diariz.Worker/callback.py` (`post_live_chunk_result`)
- Modify: `src/Diariz.Worker/pipeline.py` (a window-transcribe entry point)
- Test: `src/Diariz.Worker/tests/test_worker.py`, `src/Diariz.Worker/tests/test_pipeline.py`

- [ ] **Step 1: Write the failing tests**

- `test_handle_live_chunk_prepends_the_previous_tail` - when `PrevBlobKey` is set, the decoded input is the previous chunk's last `OverlapMs` followed by this chunk. **Byte-joined, not opened separately** - the same constraint phase 1 found the hard way: a chunk after the first has no WebM header, so anything that opens one on its own fails with *"EBML header parsing failed"*. Reuse `audio_merge.join_bytes`.
- `test_handle_live_chunk_trims_results_back_to_its_own_span` - segments that fall inside the prepended overlap are discarded, so the same words are not reported twice. Assert on the segment list, not on a duration.
- `test_handle_live_chunk_offsets_into_recording_time` - the worker returns times relative to the recording, not the chunk, so the API stores them without arithmetic.
- `test_handle_live_chunk_reports_failure_without_killing_the_loop` - one bad chunk must not stop the meeting.
- `test_run_loop_prefers_live_chunks_over_a_queued_full_transcription` - the priority read of spec §6.3.

- [ ] **Step 2: Watch them fail, then implement**

The priority read is a non-blocking `XREADGROUP` on the live stream before the existing blocking multi-stream read. Keep it small and obvious: it is the one piece of scheduling in the worker, and a subtle version of it would be hard to reason about at 3am.

**Diarization stays on** for this phase even though nothing displays its labels yet, because the callback needs the per-speaker ECAPA embeddings that phase 3 stitches. A 30 s chunk costs ~2.7 s measured, so it is affordable; turning it off now and back on in phase 3 would mean two changes to the same code path and no saved work.

- [ ] **Step 3: Mutation-verify the trim and the priority read, commit**

Break the trim so overlap segments survive, and watch the duplicate-words test fail. Then make the priority read blocking-first and watch the ordering test fail.

---

### Task 4: `POST internal/transcriptions/live-chunk` - persisting a chunk's segments

**Files:**
- Create: `src/Diariz.Api/Controllers/LiveChunkCallbackController.cs`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (`PutChunk` also enqueues the live job)
- Modify: `src/Diariz.Api/Hubs/TranscriptionHub.cs` (`NotifyLiveTranscriptAsync`)
- Test: `tests/Diariz.Api.Tests/LiveChunkCallbackTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

- `FirstChunk_CreatesTheProvisionalTranscription` - version 1, `IsProvisional = true`, `Model` naming the live pass.
- `LaterChunks_AppendToTheSameProvisionalTranscription` - not a new version each time.
- `Segments_AreOrderedByTheirRecordingTime_NotArrivalOrder` - chunks can complete out of order under retry, and `Ordinal` has to reflect the meeting, not the queue.
- `ARedeliveredChunk_ReplacesItsSegments_RatherThanDuplicating` - Redis streams are at-least-once, so this **will** happen in production.
- `NoSegments_IsNormalMidMeeting_AndDoesNotFailTheRecording` - spec §9.7: silence is not the whole-recording "no speech detected" failure.
- `WrongSecret_IsUnauthorized`.
- `SpeakerEmbeddings_AreStoredButNoLabelIsAssigned` - phase 2 keeps the vectors for phase 3 and shows nothing.

- [ ] **Step 2: Watch them fail, implement, then mutation-verify redelivery and ordering, commit**

The redelivery mutation is the important one: make the handler append unconditionally and watch the duplicate test fail. At-least-once delivery means an append-only handler produces a transcript with sentences repeated at random, which reads as a transcription bug rather than a queue one and would be miserable to diagnose.

---

### Task 5: Lag detection and graceful degradation

Spec §9.6. Written as its own task because it is the difference between "the live transcript is broken"
and "the live transcript is behind, and the meeting is unaffected".

**Files:**
- Create: `src/Diariz.Api/Services/LiveTranscriptLag.cs` (pure)
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs`, `src/Diariz.Api/Configuration/AppOptions.cs`
- Test: `tests/Diariz.Api.Tests/LiveTranscriptLagTests.cs` (new)

- [ ] **Step 1: Write the failing tests against the pure part**

`ShouldPauseLiveTranscription(oldestUntranscribedChunkAge, maxLag)` plus the boundary case **exactly at**
the threshold. Phase 1 learned that lesson: a `>` / `>=` mutation survives unless the boundary itself is
a case.

- [ ] **Step 2: Implement, and make the degradation loud rather than silent**

When lag exceeds the limit the API stops enqueueing live jobs for that recording and emits
`LiveTranscriptDegraded`. **Chunk upload and finalise are untouched** - assert that in a test, because
it is the whole promise of this feature and the easiest thing to break while adding a pause.

- [ ] **Step 3: Mutation-verify, commit**

---

### Task 6: Integration tests against real infrastructure

**Files:**
- Create: `tests/Diariz.Api.IntegrationTests/LiveTranscriptFlowIntegrationTests.cs`

- [ ] **Step 1: Write the tests**

- A three-chunk flow producing one provisional transcription with segments in recording order, on real Postgres.
- The provisional transcription is invisible to search and to the MCP tools, against a real database rather than a fake.
- The final pass supersedes it: after finalise, `GET /api/recordings/{id}` returns the non-provisional version, and the provisional one is still there as an older version.

That last one is the load-bearing claim of the whole design (spec D3) and has never been asserted against a real relational query.

- [ ] **Step 2: Green, commit**

---

### Task 7: `liveTranscript.ts` - the client-side transcript model

**Files:**
- Create: `apps/web/src/lib/liveTranscript.ts`, `apps/web/src/lib/liveTranscript.test.ts`

Pure. Applies append events to an ordered transcript.

- [ ] **Step 1: Write the failing tests**

- Segments order by recording time regardless of the order events arrive.
- A re-delivered sequence replaces rather than duplicates (the client sees at-least-once too).
- An event for a different recording is ignored.
- An out-of-order event does not truncate what is already shown.
- The model reports how far behind it is, for the status line.

- [ ] **Step 2: Watch them fail, implement, mutation-verify, commit**

---

### Task 8: The Transcript tab in the notes panel

**Files:**
- Modify: `apps/web/src/components/hub/NotesPopover.tsx`, `apps/web/src/pages/NotesPopout.tsx`
- Modify: `apps/web/src/lib/notesChannel.ts` (carry the transcript into the pop-out)
- Modify: `apps/web/src/lib/signalr.ts`, `apps/web/src/lib/api.ts`
- Modify: `apps/web/src/locales/*/*.json`
- Test: the matching `.test.tsx` files

- [ ] **Step 1: Write the failing component tests**

- The tab appears only while a recording is running.
- Text arrives and renders as it is pushed.
- **No speaker labels are shown.** Assert this explicitly and comment why: per-chunk labels are meaningless across chunks until phase 3 stitches them, and showing them would be worse than showing nothing. This test is what stops a well-meaning later change from switching them on early.
- The lag indicator renders, and the degraded state says the transcript will be complete after the meeting rather than showing an error.
- It survives into the pop-out window.

- [ ] **Step 2: Watch them fail, implement, run the suite on Linux, commit**

---

### Task 9: Chat over a running meeting

**Files:**
- Modify: `src/Diariz.Api/Services/ChatContextBuilder.cs`
- Modify: `apps/web/src/components/ChatPanel.tsx` (entry point from the in-meeting surface)
- Test: `tests/Diariz.Api.Tests/ChatContextBuilderTests.cs`, `apps/web/src/components/ChatPanel.test.tsx`

- [ ] **Step 1: Write the failing tests**

- The provisional transcript is included in the prompt.
- It is **marked as a meeting in progress**, so the model does not describe a partial discussion as concluded. Assert the marker text is present - this is a one-line change that is easy to drop and impossible to notice from the outside.
- No RAG: `EmbeddingWorker` is not enqueued (D5), and the transcript is pre-loaded whole.

- [ ] **Step 2: Implement, mutation-verify the marker, commit**

`ChatController` itself needs no change - it loads the current transcription's segments ordered by `Ordinal` with no status filter, so a provisional transcription is picked up as-is.

---

### Task 10: Docs and the release checklist

- [ ] **Step 1: Version + seven mirrors**, `RECENT[0]` entry, README Features row, `docs/features.md`, About-box `CAPABILITIES` - in lockstep.
- [ ] **Step 2: `docs/Overall_Synopsis_of_Platform.md`** - the twelfth stream, the live-chunk contract both ways, the new callback route, the priority read, and the `LiveTranscriptAppended` / `LiveTranscriptDegraded` events.
- [ ] **Step 3: `docs/Data_Schema.md`** - `Transcription.IsProvisional` and its migration, noting no `CurrentFormat` bump.
- [ ] **Step 4: A help article** on what the live transcript is, that it is provisional, that **it does not name speakers yet**, and what happens when it falls behind.
- [ ] **Step 5: Commit**

---

### Task 11: Live verification and the pull request

- [ ] **Step 1: Full suites** - `dotnet test`, the web suite **on Linux**, `python -m pytest`, and the n8n node.
- [ ] **Step 2: Verify against a real stack.** Phase 1 found a defect this way that no test had. Bring up `deploy/`, **rebuild the worker image** (the live handler is new Python), then drive a capture and watch: text appearing before Stop; a chunk failing and the transcript continuing; the transcript going quiet under forced lag while capture continues; and the final pass superseding the provisional version.
- [ ] **Step 3: Open the PR**, stating **server redeploy + worker image rebuild**, no desktop release. Then correct the `pr:` field to the real number.

## Self-review

- Every task has a failing test seen to fail before its implementation.
- Every gate in §7.2 is mutation-verified individually.
- Boundary cases are tested at the boundary, not near it.
- **No speaker labels reach the screen in this phase**, and a test enforces it.
- Chunk upload, finalise and the final transcript are untouched by every failure path here.
