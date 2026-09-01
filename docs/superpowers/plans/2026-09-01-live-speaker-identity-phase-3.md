# Live Speaker Identity (Phase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A speaker keeps one label for the whole meeting, and a colleague whose voice is enrolled is named rather than numbered. This is what turns phase 2's wall of text into a transcript you can read.

**Architecture:** pyannote clusters over whatever audio it is given, so its labels are only meaningful *within* one chunk. The worker already returns a 192-d ECAPA vector per chunk-local speaker. A new pure `LiveSpeakerStitcher` keeps a running centroid per **session** label and matches each chunk's speakers against it by cosine distance, minting a new session label when nothing is close enough. Independently, each session label is ranked against the platform voiceprint directory through the existing `ISpeakerIdentifier` + `IdentificationRules`, so a known voice gets a real name.

**Tech Stack:** ASP.NET Core 10 + EF Core (Npgsql + pgvector), xUnit + Testcontainers, React 19 + TypeScript, vitest.

**Spec:** [docs/Streaming_Capture_and_Live_Transcript.md](../../Streaming_Capture_and_Live_Transcript.md) - this plan implements **PR 3 of §15**, specifically §6.4.

**Depends on [phase 2](2026-09-01-live-transcript-phase-2.md)**, which must be merged first. Phase 2 stores the per-chunk ECAPA vectors and deliberately shows no speaker labels at all; this phase is what makes labels meaningful and turns them on. The two plans were written together so that boundary is deliberate rather than accidental.

## Global Constraints

All of phase 2's constraints apply unchanged. In addition:

- **Branch:** create `claude/live-speaker-identity-phase-3` off `origin/main` **after phase 2 has merged**.
- **Target version:** current `version.json` **+ one minor**. Confirm rather than assuming.
- **A live match must never enrol.** This is the hard rule of the whole identification design, restated in spec §6.4 and in `CLAUDE.md`: automatic identification does not write a `VoiceSample`. Live matches are the worst possible enrolment input - provisional text, short windows, a centroid still forming - and enrolment is **platform-wide**, so a bad one changes recognition for every user of the instance, not just this recording. There is a task below whose only job is to prove this cannot happen.
- **Identification is platform-wide and so is every voiceprint.** `ISpeakerIdentifier.RankAsync` scans every `Person` with an embedding who has not opted out, with no owner filter. Nothing in this phase may narrow or widen that.
- **`ISpeakerIdentifier` returns evidence, not verdicts.** It applies no threshold; deciding what a distance means belongs to `IdentificationRules.Decide`. Do not add a second threshold anywhere - the operating point has to stay one number an administrator can calibrate.
- **No new schema.** `Speaker.Embedding` is already `vector(192)` and `Speaker.PersonId` / `IdentifiedAuto` already exist. If this phase seems to need a column, re-read §6.4 first.

---

### Task 1: `LiveSpeakerStitcher` - the pure matching decision

Everything hard about this phase is in this one pure function, and it is testable with hand-built
vectors and no database, no GPU and no audio.

**Files:**
- Create: `src/Diariz.Api/Services/LiveSpeakerStitcher.cs`
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs` (`Live:StitchThreshold`, `Live:StitchMargin`)
- Test: `tests/Diariz.Api.Tests/LiveSpeakerStitcherTests.cs` (new)

**Interfaces:**
- Produces: `Stitch(IReadOnlyList<SessionCentroid> known, IReadOnlyList<ChunkSpeaker> incoming, StitchThresholds t) -> IReadOnlyList<StitchDecision>`, where a decision is either "this chunk label is that session label" or "mint a new one".

- [ ] **Step 1: Write the failing tests**

Build vectors by hand so the distances are known rather than discovered:

```csharp
[Fact] public void AVoiceCloseToAKnownCentroid_JoinsIt() { }
[Fact] public void AVoiceCloseToNothing_MintsANewSessionLabel() { }
[Fact] public void AVoiceCloseToTwoCentroids_WithNoClearWinner_MintsRatherThanGuessing() { }
[Fact] public void ExactlyAtTheThreshold_DoesNotMatch() { }      // the boundary IS a case
[Fact] public void ExactlyAtTheMargin_DoesNotMatch() { }
```

Then the two the measurement says will actually happen:

```csharp
[Fact]
public void ThreeChunkLabelsCanCollapseOntoTwoSessionLabels()
{
    // Measured during the S0 benchmark: pyannote found THREE speakers in a 20 s clip containing two.
    // Short-window clustering over-segments, so the stitcher must never assume a bijection between
    // chunk labels and session labels.
}

[Fact]
public void TwoChunkLabelsNeverCollapseOntoTheSameSessionLabelInOneChunk()
{
    // The opposite error, and the one that reads worst: pyannote already decided these are two
    // different voices in this chunk. Merging them would put two people's words under one name.
}
```

- [ ] **Step 2: Watch them fail, implement, then mutation-verify all five thresholds/rules**

Flip `<` to `<=` on the threshold; drop the margin check; allow the bijection assumption; allow two
chunk labels onto one session label. Each must fail exactly its own test.

- [ ] **Step 3: Commit**

---

### Task 2: The running centroid

**Files:**
- Modify: `src/Diariz.Api/Services/LiveSpeakerStitcher.cs` (`UpdateCentroid`)
- Test: `tests/Diariz.Api.Tests/LiveSpeakerStitcherTests.cs`

- [ ] **Step 1: Write the failing tests**

- A centroid is the mean of the vectors seen for that label, re-normalised - so it stays comparable with the vectors it is matched against.
- Adding a vector moves the centroid toward it, and the amount it moves shrinks as more are added. Assert the *direction and diminishing size*, not a magic number.
- A centroid built from one noisy chunk is not treated as more authoritative than one built from ten. Spec §6.4 records the measured reason: ECAPA on 15-30 s of one voice is noisy, and that is the real floor under chunk length.

- [ ] **Step 2: Implement, mutation-verify (drop the re-normalisation and watch matching drift), commit**

---

### Task 3: Applying the stitch in the callback

**Files:**
- Modify: `src/Diariz.Api/Controllers/LiveChunkCallbackController.cs`
- Test: `tests/Diariz.Api.Tests/LiveChunkCallbackTests.cs`

- [ ] **Step 1: Write the failing tests**

- A chunk's segments are relabelled from chunk-local to session labels before they are stored.
- A `Speaker` row exists per session label, carrying the running centroid on `Speaker.Embedding`.
- **A relabel is retroactive and is pushed.** When chunk 5 reveals that what was two session labels is one voice, earlier segments are updated and the client is told - otherwise the transcript keeps a split that the server no longer believes.
- A re-delivered chunk does not double-count into the centroid. At-least-once delivery means the same vector will arrive twice, and a centroid that absorbs it twice is quietly wrong in a way no test of a single chunk would catch.

- [ ] **Step 2: Implement, mutation-verify the retroactive relabel and the double-count guard, commit**

---

### Task 4: Naming a known voice

**Files:**
- Modify: `src/Diariz.Api/Controllers/LiveChunkCallbackController.cs`
- Test: `tests/Diariz.Api.Tests/LiveSpeakerNamingTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

- A session label whose centroid matches an enrolled person is named, via `ISpeakerIdentifier.RankAsync` + `IdentificationRules.Decide` - **the same two calls the finished-recording path uses**, not a parallel copy with its own numbers.
- A person who has opted out is never matched.
- A suggestion in the confirm band is offered rather than asserted, exactly as it is for a finished recording.
- **A manually-named speaker is never overridden**, matching the existing rule for the completed path.
- The identification decision is re-taken as the centroid improves, so a voice named wrongly early can be corrected by later evidence.

- [ ] **Step 2: Implement, mutation-verify, commit**

---

### Task 5: The rule that a live match never enrols

Its own task, with its own file, because it is the one mistake here that would damage **other users'
data on the whole instance** rather than this recording.

**Files:**
- Test: `tests/Diariz.Api.Tests/LiveIdentificationNeverEnrolsTests.cs` (new)
- Modify: whatever Task 4 got wrong

- [ ] **Step 1: Write the failing tests**

- Processing a chunk whose speaker matches an enrolled person writes **no** `VoiceSample`.
- It does not call `PeopleDirectory.RecomputeVoiceprintAsync`, so no shared centroid is rebuilt.
- The same holds when the match is confident, when it is a suggestion, and when the person has no voiceprint yet.
- A user **confirming** a live speaker by hand *does* enrol, through the existing `ISpeakerAssignment.AssignAsync` - the rule is about automatic matches, not about people.

- [ ] **Step 2: Mutation-verify by making the callback enrol on a confident match, and watch these fail**

If they do not fail, they are not testing what they claim, and this is the one place in the plan where a
guard that only looks right is genuinely dangerous.

- [ ] **Step 3: Commit**

---

### Task 6: Integration tests with real pgvector

**Files:**
- Create: `tests/Diariz.Api.IntegrationTests/LiveSpeakerIdentityIntegrationTests.cs`

- [ ] **Step 1: Write the tests**

The `vector(192)` cosine match is Postgres-only - it is faked in the unit project by necessity, so the
real ranking has only ever been exercised here.

- A live centroid stored and read back through `Speaker.Embedding` keeps its value.
- Ranking a live centroid against enrolled people returns the right person, ordered by real cosine distance.
- Relabelling a whole meeting's segments is one statement, not one per segment - a 90-minute meeting has thousands.

- [ ] **Step 2: Green, commit**

---

### Task 7: Showing the labels

**Files:**
- Modify: `apps/web/src/lib/liveTranscript.ts`, `apps/web/src/components/hub/NotesPopover.tsx`
- Modify: `apps/web/src/locales/*/*.json`
- Test: the matching `.test.ts(x)` files

- [ ] **Step 1: Write the failing tests**

- Labels render, and a named person shows their name rather than `SPEAKER_01`.
- **A retroactive relabel updates lines already on screen** - the transcript must not keep a split the server has abandoned.
- A label that is a suggestion rather than a confident match is visually distinguished, so a wrong guess is legible as a guess.
- **Delete phase 2's "no speaker labels are shown" test in this task, deliberately and in the same commit that makes labels meaningful.** Leaving it would fail; deleting it earlier would remove the guard while it still mattered.

- [ ] **Step 2: Implement, run the suite on Linux, commit**

---

### Task 8: Docs and the release checklist

- [ ] **Step 1:** Version + seven mirrors, `RECENT[0]`, README row, `docs/features.md`, `CAPABILITIES` - in lockstep.
- [ ] **Step 2:** `docs/Overall_Synopsis_of_Platform.md` - the stitcher, the two new settings, and a restatement that a live match never enrols.
- [ ] **Step 3:** Update the phase 2 help article: it says speakers are not named yet, and that stops being true here.
- [ ] **Step 4:** Commit.

---

### Task 9: Live verification and the pull request

- [ ] **Step 1: Full suites**, including the web suite **on Linux**.
- [ ] **Step 2: Verify against a real stack** with a genuinely multi-speaker recording - two synthetic voices is the minimum, and `tools/transcription-bench/make-audio.ps1` already generates exactly that. Watch: labels staying stable across chunk boundaries; an enrolled voice being named; a relabel arriving retroactively; and - the one that matters most - **`VoiceSamples` unchanged in the database afterwards**.
- [ ] **Step 3: Open the PR**, stating server redeploy + worker rebuild only if the worker changed (it should not in this phase), then correct the `pr:` field.

## Self-review

- The hard part is one pure function, tested with hand-built vectors.
- Both over-segmentation and under-segmentation are tested, because the measurement showed the first actually happens.
- Boundary cases are tested **at** the boundary.
- The never-enrol rule has its own file, its own mutation, and a database assertion in live verification.
- Identification uses the same `RankAsync` + `Decide` pair as the finished-recording path, with no second threshold introduced anywhere.
- Phase 2's "no labels" guard is deleted in the same commit that makes labels meaningful, not before.
