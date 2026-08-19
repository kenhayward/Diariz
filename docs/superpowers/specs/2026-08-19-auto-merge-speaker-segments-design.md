# Auto-merge speaker segments after transcription

**Date:** 2026-08-19
**Status:** approved, ready to plan
**Deployment surface:** server redeploy (API + web). No desktop release - no `apps/desktop` file is touched.
**Version:** 0.228.4 -> 0.229.0 (functional enhancement: Minor +1, Build reset)

## Problem

Diarization emits a new segment every time the speaker changes, and WhisperX splits further on pauses. A
transcript therefore arrives fragmented: a single person's uninterrupted two-minute answer can land as
twenty rows. The user's report is that "transcript speakers are always merged" - meaning the merge is
something they always end up performing, by hand, on every recording.

The remedy already exists as a manual action: the transcript toolbar's **Merge rows** button
(`POST /api/recordings/{id}/merge-segments`). It is correct and well-tested. It is simply not automatic,
so it has to be clicked once per recording, forever.

## Goals

1. A per-user preference that runs that same merge automatically, once a recording's transcription and
   voiceprint identification have completed.
2. Default **off**, so existing behaviour is unchanged for every current user until they opt in.
3. Auto-merged output must be **identical** to what the manual button produces - one merge implementation,
   not two.

## Non-goals

- Changing the manual **Merge rows** button, its endpoint, or its output.
- Any per-room, per-folder or per-recording scope. The setting is per user, as requested.
- An un-merge. Merging remains permanent for a transcription version (see the decisions below).
- Changing `SegmentMerger` itself. The pure merge rule is correct and stays untouched.

## Decisions taken during design

### It runs on every transcription, including re-transcribes

Merging is permanent for a transcription version, and today the documented way back to granular segments
is to re-transcribe. With the setting on, that escape hatch is closed: a re-transcribe produces a merged
transcript too.

This was considered and accepted. The alternatives were worse:

- *First transcription only* makes one setting behave differently depending on invisible version state.
- *A per-recording override on the Retranscribe modal* threads a flag from the modal through the job
  payload to the callback, for an escape hatch that turning the preference off already provides.

The route back to granular segments is therefore: **turn the preference off, then re-transcribe**. The
endpoint's `EndpointDescription` and the help article are updated to say so, because both currently state
"re-transcribe to get granular segments back" without qualification.

### RAG chunks get coarser, and that is accepted

`TranscriptChunker` windows segments into ~1200-character retrieval chunks but **never splits a segment** -
"a chunk always holds at least one whole segment, so it may slightly exceed this when a single segment is
large" (`TranscriptChunker.cs:20`). Merged segments are therefore coarser retrieval units.

This is a genuine difference from the manual button, not just a restatement of it. Clicking **Merge rows**
today does not re-enqueue embeddings, so existing chunks stay granular. Auto-merge runs *inside* the worker
callback, and the embedding job is enqueued a few lines later, so chunks are built from the merged segments.
A ten-minute monologue becomes one ~8,000-character segment and therefore one ~8,000-character chunk instead
of roughly seven windowed ones.

Accepted rather than mitigated. Capping merged block size would make auto-merge produce different output
from the manual button, breaking goal 3; preserving the pre-merge shape for the embedding job alone needs
that shape persisted or carried through the job payload, which is real machinery for a secondary effect.
The trade-off is stated in the preference's own helper text and in the help article.

Jump-to-time granularity in the transcript view coarsens for the same reason: clicking a merged block seeks
to the start of the whole run.

### One code path, via a static helper

`RecordingsController` is hand-constructed at **8 sites** across the test projects, so adding a constructor
dependency is pure churn. The codebase already has the right pattern for exactly this situation:
`SpeakerLabeling` is a static helper that mutates entities in place and is documented as "shared by the
worker callback and the on-demand Re-identify action". Auto-merge is the same shape and uses the same
pattern.

Rejected: building the merged shape in memory before the callback's first save. It avoids one round of
inserts, but the key function needs the *unsaved* `Speaker` rows whose `PersonId` was just set by
`SpeakerLabeling`, which a database-loading helper cannot see - so it forks into two derivations of the
same merge decision. This repository has been bitten by that shape before (a value written by both a live
path and a backfill, each side's test passing and neither proving the pair agrees). Saving first and then
merging costs one wasted round of inserts per transcription and buys a single, already-tested code path.

## Design

### 1. Storage and API

`UserSettings.AutoMergeSpeakerSegments` - `bool`, default `false`, alongside the existing recording
preferences. Migration `AddAutoMergeSpeakerSegments`.

The migration is additive with a default, so an older backup restores cleanly into it.
**`MaintenanceController.CurrentFormat` is not bumped.**

`UserSettingsDto` (`ApiDtos.cs:633-637`) gains `bool AutoMergeSpeakerSegments = false`;
`UpdateUserSettingsRequest` (`ApiDtos.cs:664-670`) gains `bool? AutoMergeSpeakerSegments = null`. The
nullable form is the established tri-state that lets each Preferences tab save only its own fields.
`UserSettingsController` reads `s?.AutoMergeSpeakerSegments ?? false` on GET and
`if (req.AutoMergeSpeakerSegments is { } v) s.AutoMergeSpeakerSegments = v;` on PUT, mirroring
`CalendarAutoStopEnabled` line for line.

Web `types.ts`: `autoMergeSpeakerSegments: boolean` on the settings type (near line 527) and
`autoMergeSpeakerSegments?: boolean` on the update type (near line 959).

### 2. The shared helper

New `src/Diariz.Api/Services/TranscriptSegmentMerge.cs`:

```csharp
public static class TranscriptSegmentMerge
{
    public static Task<bool> ApplyAsync(
        DiarizDbContext db, Guid recordingId, Guid transcriptionId, CancellationToken ct = default);
}
```

It holds everything `RecordingsController.MergeSegments` does *below* its ownership check and
current-transcription lookup:

1. load the transcription's segments ordered by `Ordinal`;
2. build the speaker key map (assigned person -> display name -> raw label);
3. compute the `BreakBefore` index set from meeting-note and screenshot capture times via
   `TranscriptNoteAnchor`;
4. run `SegmentMerger.Merge`;
5. if nothing collapsed, return `false` without touching the change tracker;
6. otherwise `RemoveRange` the originals, `Add` the merged rows with fresh ordinals, return `true`.

It does **not** call `SaveChangesAsync` - the caller does, matching `SpeakerLabeling.ApplyAsync`.

`RecordingsController.MergeSegments` keeps its ownership check, its highest-version lookup and all three
of its `NotFound()` cases, then delegates and saves. Its observable behaviour is unchanged, and its four
existing tests are the regression guard for the extraction.

### 3. The pipeline hook

In `WorkerCallbackController.Result`, immediately after the existing `await _db.SaveChangesAsync()` at
`WorkerCallbackController.cs:138` and **before** the `if (autoSummarise)` block at line 140:

```csharp
if (autoMerge)
{
    try
    {
        if (await TranscriptSegmentMerge.ApplyAsync(_db, transcription.RecordingId, transcription.Id))
            await _db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Auto-merge failed for transcription {TranscriptionId}", transcription.Id);
    }
}
```

where `autoMerge` is read from the **recording owner's** settings
(`_db.UserSettings.FindAsync(transcription.Recording.UserId)`, `?? false` when the row does not exist -
the settings row is created lazily, so plenty of users have none).

That position matters, and three things follow from it:

- **It is after `SpeakerLabeling.ApplyAsync` (line 111) and its save.** So the merge groups by identified
  person: two diarization labels that voiceprinting resolved to the same person merge together. This is
  what "same speaker" must mean once the voiceprint pipeline has run, and it is the sense in which the
  merge happens "after the pipeline finished".
- **It is before every downstream enqueue** (summary, actions, tags, embeddings) and before the SignalR
  notify and webhook further down. So all of them see the final shape, and no client is ever told about
  the unmerged one - there is no visible reshuffle.
- **Notes and screenshots are already boundaries.** They are captured *during* the recording, so they
  exist by callback time, and reusing the endpoint's path picks up their `BreakBefore` handling for free.

The `try`/`catch` is deliberate. Auto-merge is a presentation nicety and the unmerged transcript is
perfectly valid; a throw at this point would leave the recording committed as `Summarizing` with no
summarization job enqueued, stranding it in "Summarising..." forever - the exact failure the adjacent
enqueue guard at lines 140-160 already exists to prevent.

`WorkerCallbackController` gains an `ILogger<WorkerCallbackController>`. It has only 2 hand-construction
sites, both in tests.

### 4. UI

A third card in `apps/web/src/components/RecordingsSection.tsx`, after the calendar auto-stop card, reusing
that card's layout (glyph + heading + body on the left, `role="switch"` button on the right - a native
checkbox cannot be styled as a track and knob without losing the focus ring). No sub-fields, so nothing is
revealed when it is on.

Copy: a heading naming the behaviour ("Merge each speaker's turn into one block"), and body text covering
what happens, when it happens (automatically once a recording finishes transcribing), that notes and
screenshots remain boundaries, and that it is permanent for that transcription - to switch back, turn this
off and re-transcribe.

The component's `Baseline` interface grows a sixth field. Its two comments - "The five values as last
loaded or last saved" and "The exact five fields Save sends" - are corrected in the same edit. `dirty`,
`onSave` and the seeding block each gain one line.

Strings go into **all four** locale catalogs (`en`, `de`, `es`, `fr`); `locales.test.ts` enforces key
parity and fails the build on a missing translation. Per project convention: plain hyphens only, no
em/en dashes.

## Error handling

| Case | Behaviour |
|---|---|
| Setting off (default) | No merge. Byte-identical to today's pipeline. |
| Owner has no `UserSettings` row | Treated as off. |
| Transcription produced no segments | The callback's existing no-speech branch returns at line 126, before the hook. Never reached. |
| Nothing adjacent to merge | Helper returns `false`; no save, no churn. |
| Merge throws | Logged; the pipeline continues with the unmerged transcript. The recording never strands. |

## Testing

Red first, per the project's TDD rule. The default-off case is a first-class test, not an afterthought -
it is the guarantee that existing users see no change.

**`WorkerCallbackControllerTests`** (unit, in-memory + fakes)

- ON: consecutive same-speaker segments collapse into one.
- OFF: segments stay granular. Pins the default.
- ON with no `UserSettings` row: granular. Null-safety.
- ON: two diarization labels that the fake `ISpeakerIdentifier` maps to one person merge together. Pins
  that the merge runs *after* voiceprint identification, which is the whole point of the hook's position.

**`RecordingsControllerTests`** - the four existing `MergeSegments_*` tests are kept unchanged and now
exercise the extracted helper through the endpoint. They are the regression guard for the extraction; they
must pass without edits.

**Integration** (`Diariz.Api.IntegrationTests`) - the callback path merges correctly against real
Postgres. The in-memory provider does not faithfully translate ordering, and this path depends on
`OrderBy(s => s.Ordinal)`; `ScreenshotMergeBreakTests` is the precedent for testing merge behaviour at
this layer.

**`UserSettingsControllerTests`** - GET/PUT round-trip; a PUT omitting the field leaves it alone
(tri-state).

**`RecordingsSection.test.tsx`** - renders off when settings say off; flipping the switch marks the footer
dirty; Save asserts `updateUserSettings` was **called with** `autoMergeSpeakerSegments: true`. Asserting
the call, not relying on the method's absence from the `vi.mock` factory - an absent-method guard is
destroyed silently by any later change that needs the method.

No web test may add a `jest-dom` dependency: none of the 230+ existing web test files use its matchers.

## Release checklist

1. `version.json` -> `0.229.0`, plus all four mirrors: `apps/web/package.json`,
   `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`,
   `integrations/n8n-nodes-diariz/package.json`.
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`, with the real PR number (it cannot be guessed as
   last + 1; issues and Dependabot share the sequence and no test catches a wrong value).
3. `CAPABILITIES` table row in `releases.ts` - a new user-facing capability.
4. README **Features** table row.
5. `docs/features.md` prose bullet, in lockstep with 4.
6. `docs/Overall_Synopsis_of_Platform.md` - the worker-callback flow gains a step.
7. `docs/Data_Schema.md` - the new `UserSettings` column and a migration-history row.
8. Help article `apps/web/src/content/help/en/transcription-and-speakers.md` - the **Merge** bullet
   (line 89) gains that this can be automatic, and the "re-transcribe to get granular segments back"
   guidance (line 35 area and the endpoint description) is qualified. ASCII only.
9. The OpenAPI snapshot test rewrites its own snapshot: run once to regenerate, once to pass, and commit
   the regenerated file.
