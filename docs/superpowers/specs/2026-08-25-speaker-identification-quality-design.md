# Speaker Identification Quality - Design

**Date:** 2026-08-25
**Status:** Approved design, not yet implemented.
**Supersedes nothing.** Builds directly on the voiceprint management work shipped in 0.249.0
(PR #608) and on the research note `docs/Speaker_Identification_and_Verification.md`.

---

## 1. Why

Auto-identification is precise but has poor recall: when it fires it is almost always right, and it
rarely fires. Three measurements against the live instance on 2026-08-25 explain why, and each one
points at a different fix.

### 1.1 Distance distribution

1,125 speakers carry an ECAPA embedding; 532 are linked to a person. Cosine distance from each
speaker to its nearest person voiceprint:

| Distance | Speakers | Linked |
|---|---|---|
| 0.0 - 0.1 | 71 | 70 |
| 0.1 - 0.2 | 128 | 124 |
| 0.2 - 0.3 | 326 | 293 |
| 0.3 - 0.4 | 105 | 15 |
| 0.4 - 0.5 | 80 | 7 |
| 0.5 - 0.6 | 148 | 2 |
| 0.6 - 0.7 | 153 | 1 |
| 0.7 - 0.8 | 76 | 0 |
| 0.8 - 0.9 | 9 | 0 |

The true-match mode sits around 0.2 - 0.3 and the impostor mass around 0.55 - 0.75, with a valley
near 0.45. The configured acceptance threshold is 0.4.

### 1.2 Finding A - matches are never revisited

**128 speakers sit inside the 0.4 threshold, are still anonymous (`DisplayName = Label`, so nothing
manual is blocking them), and are unlinked.** Identification runs exactly once, in the transcription
callback (`Services/SpeakerLabeling.cs`). Enrolling a person today never revisits yesterday's
recordings, and for 94 of those 128 the nearest profile's voiceprint changed *after* the recording was
made. There is no re-scan anywhere in the codebase.

This is a ~24% uplift on the current linked count with no model change at all.

### 1.3 Finding B - the centroid dilutes itself

`Services/Voiceprints.Centroid` is the L2-normalised mean of every sample. Measuring each person's own
samples against their own voiceprint:

| Samples | People | Avg distance to own centroid | Worst sample |
|---|---|---|---|
| 2 | 11 | 0.147 | 0.240 |
| 3 | 5 | 0.193 | 0.495 |
| 4 | 5 | 0.260 | 0.564 |
| 5 | 1 | 0.327 | 0.760 |
| 8 | 1 | 0.304 | 0.589 |
| 14 | 1 | 0.356 | 0.755 |

**The more a person is enrolled, the worse their voiceprint fits their own audio.** Heavily-enrolled
people have samples 0.52 - 0.76 from their own centroid, well outside the 0.4 threshold: their own
enrolled voice would fail their own voiceprint.

Two explanations are consistent with this and the system cannot currently tell them apart:

- **Device variation.** The same person on meeting-room speaker, laptop mic, phone and car audio
  produces embeddings that are genuinely far apart. Averaging them lands on a point close to none of
  them.
- **Misattribution.** A wrongly-enrolled speaker drags the centroid toward another voice.

Distinguishing the two is a primary goal of this work (section 6).

### 1.4 Finding C - nothing to calibrate from

One global `Identification:Threshold = 0.4`, compiled in, with a single comparison behind it. No
margin between best and runner-up, no minimum-duration gate, and near-misses are discarded without a
trace. There is no stored evidence from which any threshold could be justified.

### 1.5 Supporting figures

| Measure | Value |
|---|---|
| Recordings | 164 |
| Segments | 57,255 (20,956 under 2s; 8,516 over 10s) |
| Total segment audio | 109.8 hours |
| People | 91 (90 with a voiceprint) |
| Voice samples (`ProfileContributions`) | 166 |
| Speakers linked, of which manual | 532, of which 176 manual |
| Users | 2 |
| `Segments` table size | 32 MB |
| `Segments.Embedding vector(768)` populated rows | **0** (vestigial; RAG uses `TranscriptChunks`) |

The 176 manually-linked speakers are the only trustworthy ground truth today. The 356 auto-linked ones
are the system's own output and cannot be used to grade it.

---

## 2. Goals and non-goals

**Goals**

1. Recover the matches that already qualify but were never applied.
2. Stop a person's voiceprint degrading as they are enrolled more.
3. Make acceptance behaviour tunable at runtime, with evidence rather than guesswork.
4. Let a user hear the audio behind any voiceprint decision.
5. Let a user see and control exactly which audio trains a voiceprint.
6. Provide a test bench that measures identification quality honestly.

**Non-goals**

- Changing the embedding model. ECAPA/192-d stays. AS-norm and PLDA (research note section 5) are
  explicitly out of scope; revisit only if calibration shows cosine is the limiting factor.
- Per-person threshold overrides. Considered and rejected: per-person fiddling substitutes for fixing
  the underlying template.
- Re-diarization or any change to how pyannote splits speakers.
- Real-time / live identification.

---

## 3. Architecture

### 3.1 Scoring core

**Templates replace the single centroid.** A person gains a set of voiceprint templates, each the
centroid of an acoustically coherent cluster of that person's samples. Matching scores a probe against
every template and takes the best, attributing it to the template's owner.

```
today                              after
Person -> one mean vector          Person -> [Office template, Phone template, Car template]
probe vs mean                      probe vs each template, best wins
```

A distant new sample forms its **own template** instead of blurring the existing ones. This is what
makes accepting a borderline match safe, and is the mechanism by which the system learns a new device
condition: confirm the car recording once, it becomes the car template, and the next car recording
matches at ~0.15 instead of ~0.5.

**Clustering.** Agglomerative, single pass, cosine cut-off `IdentificationClusterDistance`. Written as a
pure function alongside the existing `Voiceprints.Centroid`. The largest person has 14 samples, so this
is arithmetic, not machine learning - no library, no iteration limits, fully deterministic and
unit-testable.

**`SpeakerProfiles.Embedding` is retained** as the all-samples centroid and documented as
derived-not-authoritative. It stops being what matching consults. It is kept rather than nulled so that
older backups, the MCP surface, and any external reader keep working.

**The margin is measured between people, not templates.** This is load-bearing. Ken's car template
sitting 0.05 from his office template is the system working correctly; rejecting on that would break
exactly the case being built for. The runner-up in the margin check is the best template belonging to
the *next person down*.

### 3.2 Acceptance bands

| Outcome | Condition |
|---|---|
| Auto-apply | `best <= Threshold` and `runnerUpPerson - best >= Margin` |
| Suggest | `Threshold < best <= ConfirmBand` and margin satisfied |
| Ignore | otherwise |

Speakers below `MinSpeechMs` of total speech are not scored at all. The gate is **API-side**: the API
already stores every segment's `StartMs`/`EndMs` and `SpeakerLabel`, so it can sum a speaker's speech
without any worker change, and policy stays where the knob lives.

`IsMultiSpeaker` speakers and opted-out people continue to be excluded, as today.

### 3.3 Re-scan

One function, four triggers:

| Trigger | Scope |
|---|---|
| A person's voiceprint changes (enrol, recompute, template edit) | That person against all speakers |
| A threshold, band, margin or cluster knob changes | Everything |
| Manual "Re-scan now" | Everything |
| A transcription completes | That recording (existing path) |

**Synchronous.** The existing measurement is ~0.35 ms per 1,000 gallery rows per probe; 1,125 speakers
against a few hundred templates is well under a second. A **dry-run mode** returns the same report
without writing, so the UI can state "this would apply 128 and queue 74" before committing.

**Re-scan adds; it never revokes.** Revocation of a stale auto-label stays where it is today, at
transcription time. A knob change must not mass-unlabel history. Revoking stale labels is a separate,
explicit action with its own preview.

**Guards, in order:** skip manually named or manually assigned speakers; skip `IsMultiSpeaker`; skip
speakers under `MinSpeechMs`; skip opted-out people; skip any `(speaker, person)` pair already rejected.

### 3.4 Decision log

Every accept and reject is recorded with the distance that was on offer and who decided. This is the
piece that does not exist today: a rejected suggestion at 0.47 is a **labelled hard negative**, and an
accumulating log of them is a real ROC curve rather than an inference from a histogram. The bench
calibrates from this table plus the 176 manual links.

### 3.5 Segment embeddings

Lazy and cached. A recording's segments are embedded the first time something needs to score them, and
the vectors are stored permanently. No cost on recordings never investigated, no backfill job, and
re-scoring after a knob change is a pure database query.

A **new Redis stream `segment-embed-jobs`** (the ninth), one job per recording so audio loads once.
Considered and rejected: folding this into the existing `voiceprint-jobs` handler. Same model, same
audio load, same GPU - but the callbacks have genuinely different shapes (one writes a sample, one
writes N segment vectors), and a discriminated payload both sides must branch on is worse than a second
single-shaped contract.

The API filters segments under `MinSpeechMs` before enqueueing, so the ~37% too short to score never
cost GPU time.

### 3.6 Clipped audio

A new endpoint serves **only the requested span**, so an assessor hears the segments attributed to the
person under assessment and nothing else. This requires **ffmpeg in the API image** (it is not there
today) because webm/m4a/mp3 cannot be safely byte-sliced.

The Voiceprint and Diagnostics tabs use the clip endpoint **for all playback regardless of ownership** -
one code path, and no whole-file token ever leaves those tabs. Recording Detail keeps its existing
whole-file seekable playback for owners, unchanged.

**Shell-out safety:** arguments are passed as an array, never a shell string; the only inputs are a
server-derived blob path and numeric millisecond offsets. No user-supplied string reaches ffmpeg.

---

## 4. Data model

All changes are **additive and nullable-or-defaulted**. No `MaintenanceController.CurrentFormat` bump is
expected. See section 9 for the one migration hazard.

Which phase each change lands in (see section 10):

| Change | Phase |
|---|---|
| `PlatformPermission.ManageVoiceprints = 32` | 1 |
| `ProfileContributions.ExcludedAt` | 1 |
| `PlatformSettings.IdentificationThreshold` / `ConfirmBand` / `Margin` / `MinSpeechMs` | 2 |
| `Speakers.SuggestedProfileId` / `SuggestedDistance` / `SuggestedAt` | 2 |
| `SpeakerIdentityDecisions` (new table) | 2 |
| `ProfileVoiceprints` (new table) | 3 |
| `ProfileContributions.VoiceprintId` | 3 |
| `PlatformSettings.IdentificationClusterDistance` | 3 |
| `Segments.VoiceEmbedding` / `VoiceEmbeddedAt` | 4 |

One migration per phase, all additive.

### 4.1 New table `ProfileVoiceprints`

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `ProfileId` | uuid FK -> `SpeakerProfiles`, cascade | |
| `Name` | varchar(128) null | User-given ("Meeting room", "Car"). Null = unnamed auto cluster |
| `Embedding` | `vector(192)` | Cluster centroid |
| `SampleCount` | int | |
| `IsAuto` | bool | False once a user has renamed, merged or split it |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Index: `IX_ProfileVoiceprints_ProfileId`.

### 4.2 `ProfileContributions` (existing)

| Column | Type | Notes |
|---|---|---|
| `VoiceprintId` | uuid null FK -> `ProfileVoiceprints`, set null | Which template this sample belongs to. Null before Phase 3 clusters it |
| `ExcludedAt` | timestamptz null | Set when a user drops a sample from training without deleting the record of it |

### 4.3 `Speakers` (existing)

| Column | Type | Notes |
|---|---|---|
| `SuggestedProfileId` | uuid null FK -> `SpeakerProfiles`, set null | Pending suggestion |
| `SuggestedDistance` | double null | Distance on offer |
| `SuggestedAt` | timestamptz null | |

### 4.4 New table `SpeakerIdentityDecisions`

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `SpeakerId` | uuid FK -> `Speakers`, cascade | |
| `ProfileId` | uuid FK -> `SpeakerProfiles`, cascade | |
| `Decision` | int | 0 = Rejected, 1 = Accepted. **Append only, never renumber** |
| `Distance` | double | The distance at the moment of decision |
| `DecidedAt` | timestamptz | |
| `DecidedByUserId` | uuid null FK -> `AspNetUsers`, set null | |

Index: `IX_SpeakerIdentityDecisions_SpeakerId_ProfileId` (the rejected-pair guard reads it on every
re-scan).

### 4.5 `Segments` (existing)

| Column | Type | Notes |
|---|---|---|
| `VoiceEmbedding` | `vector(192)` null | Distinct from the vestigial `Embedding vector(768)` text slot |
| `VoiceEmbeddedAt` | timestamptz null | Set even when the vector is null, so "tried, nothing to store" is distinguishable from "not tried" and short segments are not retried forever |

Adds roughly 44 MB to a 32 MB table.

### 4.6 `PlatformSettings` (existing, single-row typed table)

| Column | Type | Default | Notes |
|---|---|---|---|
| `IdentificationThreshold` | double | 0.40 | Accept and auto-apply at or below |
| `IdentificationConfirmBand` | double | 0.50 | Suggest between threshold and this. **0.50 not 0.55** - at 0.55 the day-one queue is ~150 items, at 0.50 it is ~74 |
| `IdentificationMargin` | double | 0.05 | Best person must beat next person by this |
| `IdentificationMinSpeechMs` | int | 3000 | Below this, do not score |
| `IdentificationClusterDistance` | double | 0.30 | Agglomerative cut-off |

These replace the compiled `IdentificationOptions.Threshold`. `IdentificationOptions.Enabled` stays as
a server-level master switch.

### 4.7 New permission

`PlatformPermission.ManageVoiceprints = 32`. The enum is `[Flags]`; 32 is the next free bit and the
enum is append-only.

| Permission | Grants |
|---|---|
| `ManagePeople` (unchanged) | Browse, edit, merge, delete people. No cross-user audio |
| `ManageVoiceprints` (new) | Confirmation queue, bench diagnostics, re-scan, and clipped playback of segments attributed to the person under assessment |
| `ManagePlatform` (unchanged) | The threshold knobs, as `PlatformSettings` columns like every other setting |

Rationale for a new permission rather than moving people management under `ManagePlatform`: directory
hygiene (merging duplicates, fixing a company name) is routine and should stay widely delegable. Folding
it into `ManagePlatform` would mean the only people who can merge two contacts are also the people who
can restore a database over the top of production.

Rationale for granting cross-user audio at all: `MaintenanceController` already backs up "Postgres +
all object-store blobs" including audio, gated on `ManagePlatform`. A platform admin can already
download every recording in the instance in one click. In-app clipped playback is strictly narrower than
what that permission already permits, and is auditable.

---

## 5. Contracts

### 5.1 `SegmentEmbedJob` (API -> worker, `segment-embed-jobs`)

```csharp
public record SegmentEmbedSpan(Guid SegmentId, long StartMs, long EndMs);

public record SegmentEmbedJob(
    Guid RecordingId,
    string BlobKey,
    IReadOnlyList<SegmentEmbedSpan> Segments);
```

PascalCase, matching every other cross-boundary payload.

### 5.2 `SegmentEmbedResult` (worker -> API, `internal/segments/embed-result`)

```csharp
public record SegmentEmbedding(Guid SegmentId, float[]? Embedding);

public record SegmentEmbedResult(
    Guid RecordingId,
    IReadOnlyList<SegmentEmbedding> Segments);

public record SegmentEmbedFailure(Guid RecordingId, string Error);
```

A null `Embedding` means the worker could not produce one for that segment (silence, decode failure);
the API sets `VoiceEmbeddedAt` with a null vector so it is not retried.

Authenticated by `X-Worker-Secret`, like every other `internal/*` route.

**Payload size:** a 350-segment recording returns roughly 270 KB of JSON. Acceptable; revisit if a
recording is ever large enough to matter.

### 5.3 Clipped audio

```
GET /api/people/{personId}/clip?speakerId={id}&fromMs={n}&toMs={n}
```

Authorised when the caller owns the recording **or** holds `ManageVoiceprints`, and in both cases only
when the requested span falls inside a segment whose speaker is attributed to `personId`. Returns
`audio/wav`. Every cross-owner access is logged.

---

## 6. The test bench

Two distinct things, deliberately separated.

### 6.1 Global calibration (no GPU)

Sweeps threshold, band and margin across the ground truth, reporting true accepts, false accepts and
false rejects at each operating point, plus the implied EER.

**Leave-one-out is mandatory and is the correctness property the whole bench stands on.** Each
ground-truth speaker must be scored against a gallery with *its own contribution excluded from its
person's templates*. Scored against a gallery it helped build, every enrolled speaker matches itself at
~0.0 and the bench reports a spectacular accuracy that means nothing. This is the test-on-training-data
trap, and in this system it would be silent.

**Honesty requirement:** the ground truth is 176 manual links across 91 people, and negatives accumulate
only as suggestions are rejected. That is enough to be *indicative*, not authoritative. **The bench must
display its sample size next to every number.** A bare "EER 4.2%" from 176 pairs will be trusted far more
than it has earned.

### 6.2 Per-person diagnostics (GPU, lazy)

Scores every segment attributed to a person against their templates, and each of their samples against
their *other* templates, leave-one-out. This answers section 1.3 by distinguishing:

- The distant sample **clusters with others** -> genuine device variation. It becomes its own template
  and matching improves.
- The distant sample **clusters with nothing** -> almost certainly a misattributed speaker. Drop it.

It also surfaces segments *inside* a training sample that do not match the rest (crosstalk, or another
person talking over the dominant speaker - the case the segment split shipped in 0.249.0 addresses), and
segments elsewhere attributed to the person that do not match (misattributions to investigate).

### 6.3 Placement

| Surface | Permission | Contents |
|---|---|---|
| `/admin/speaker-identification` | `ManagePlatform` for knobs, `ManageVoiceprints` for the rest | Knobs, sweep curve with sample sizes, dry-run re-scan, decision log |
| Person -> Diagnostics tab | `ManageVoiceprints` | Per-person scoring results |
| Person -> Voiceprint tab | `ManagePeople` | Templates, attributions, training selection, playback |

Mirrors the `/admin/llm-models` precedent.

---

## 7. User interface

### 7.1 Person editor gains a third tab

`Profile | Voiceprint | Diagnostics`. Management in one, the bench in the other. The existing shell
(`PersonEditor.tsx`) already keeps panels **hidden rather than unmounted** so a half-typed edit survives
a tab switch; the third tab follows the same rule.

### 7.2 Voiceprint tab

**Templates are the top level.** "Voiceprints (3): Office - 5 samples, Phone - 2, Car - 1". Each expands
to its contributing recordings; each recording expands to its segments. Actions: rename, merge two
templates, split a sample into its own, exclude a sample. The distance *between* templates is shown,
because two templates 0.12 apart should probably be one.

**The candidate set becomes every attributed speaker.** Today the tab lists `ProfileContributions` - 166
rows platform-wide, only what was enrolled by hand, which is why it reads as arbitrary. It changes to
list every speaker attributed to the person (532 rows across the instance), each marked `training` or
`not training`, with how it was linked (manual, auto, confirmed) and its distance from the nearest
template. Toggling one on or off is the add/remove.

**Adding a whole speaker to training needs no worker.** `Speaker.Embedding` already exists from
transcription, so it is a database write. Only a *span subset* pays the re-embed job from 0.249.0.

**Playback everywhere there is a row.** Reusing `selectedRanges` from `lib/segmentPlayback.ts`: play a
whole sample, play the ticked selection (skipping gaps between non-adjacent picks), or play one segment.
One shared `<audio>` per tab so two rows cannot talk over each other. `VoiceSample.startMs` - present in
the DTO since voiceprints shipped, with a comment saying it exists so the UI can play a sample, and
rendered nowhere - finally gets used.

**Cross-owner rows.** The directory is platform-wide but recordings are ownership-filtered, and this is
live, not theoretical: "Ken Hayward" already spans both users across 133 speakers. Today the expand
fails silently. With `ManageVoiceprints` the clip endpoint serves the audio; without it, the row still
appears (it is genuinely part of what trained the voiceprint) but is labelled as being in an
inaccessible recording, with no play button and no segment list.

### 7.3 Confirmation queue

Two surfaces, one action:

- **A review queue** listing every pending suggestion (person, recording, distance, play button) so a
  backlog can be cleared in one sitting.
- **Inline on the unnamed speaker** in the transcript, where the words and the audio are already to
  hand.

Both call the same accept/reject endpoint. Accepting enrols the speaker as a training sample (section
3.1 explains why that is safe under clustering). Rejecting writes a decision row and leaves the speaker
anonymous.

### 7.4 Copy rules

All user-facing strings use plain hyphens, never em or en dashes. All four locale catalogs
(`en`/`de`/`fr`/`es`) must stay at exact key parity, and the non-English ones are ASCII-only.

---

## 8. Testing

Per `CLAUDE.md`, TDD throughout: failing test first, watch it fail, minimal code to pass.

| Layer | What it covers |
|---|---|
| `Diariz.Api.Tests` (unit, in-memory) | Clustering (pure), band arithmetic, margin-between-people, min-speech gate, guard ordering in re-scan, leave-one-out exclusion, clip-range authorisation |
| `Diariz.Api.IntegrationTests` (Testcontainers) | Everything touching `vector(192)`: template storage, best-template query, re-scan against real pgvector, decision-log queries, the new migrations |
| `pytest` (worker) | `segment-embed` job orchestration, span extraction, per-segment shaping, temp cleanup |
| `vitest` (web) | Tab shell, template management, candidate-set toggles, playback wiring, queue accept/reject |

**Specific traps this codebase has been bitten by, to guard against by name:**

- **Tests that cannot fail.** Every new assertion must be mutation-verified: break the production code,
  confirm the test goes red, restore. Note that restoring a `.cs` file from a backup preserves its
  mtime and MSBuild will skip the rebuild - touch the file or edit in place.
- **The in-memory provider ignores ordering and `Take` inside a filtered `Include`, and does not enforce
  FKs.** Anything depending on real relational behaviour belongs in the integration project, not a
  gamed unit test.
- **`vi.waitFor` checks once immediately**, so a "this must not arrive" assertion passes before the
  thing could have arrived. Flush a macrotask tick and assert synchronously.
- **`fireEvent.click` fires `onChange` on a disabled input.** Use `userEvent` (installed) for any
  disabled-state assertion.
- **The bench's leave-one-out** needs a test that fails when LOO is removed - i.e. one that asserts a
  known speaker does *not* score ~0.0 against its own person.

---

## 9. Migration and backup

Every schema change is additive and nullable-or-defaulted, so **no `CurrentFormat` bump is expected**.

**The one hazard:** an older backup restores with `ProfileVoiceprints` empty. Matching would then find
no templates and identify nobody. The restore path must **rebuild templates from the
`ProfileContributions` it does have** - the same clustering used at enrol time, run once per person.
This must be covered by an integration test that restores a pre-template backup and asserts matching
still works.

`Segments.Embedding vector(768)` is unpopulated across all 57,255 rows and vestigial. **Leave it
alone.** Dropping a column breaks restore of older backups and would force a `CurrentFormat` bump for no
benefit.

---

## 10. Delivery

Four phases, each independently shippable and useful, ordered by dependency.

### Phase 1 - Audio and the training set

ffmpeg in the API image; the clipped-segment endpoint; `ManageVoiceprints`; playback throughout the
Voiceprint tab; the full candidate set with add/remove; the cross-owner fix.

*Delivers the original questions 1 and 2.*

### Phase 2 - Recall recovery

Knobs into `PlatformSettings`; the confirmation band; the decision log; re-scan with dry run; queue plus
inline prompts.

*Applies the 128 waiting matches and queues ~74.*

**Phase 1 must precede Phase 2.** Confirming "is this Ken?" without being able to hear it is guesswork,
and a queue that cannot be judged is a queue that gets rubber-stamped - which would poison the decision
log, the ground truth everything downstream calibrates against, at the source.

### Phase 3 - Voiceprint diagnostics (reordered, 2026-08-25)

**This swapped places with multi-template voiceprints after Phase 2 shipped, because a measurement
contradicted the assumption clustering rests on.**

Distances between a person's *own* enrolled samples, measured on the live instance: of 108 samples belonging
to people with more than one, 33 have a close sibling (<= 0.30), 38 are loosely related (0.30 - 0.45), and
**37 sit alone (> 0.45)**. The widest same-person pair is **1.134** - essentially orthogonal, which two
recordings of one human cannot be.

The design assumed that spread was **device variation**. Some of it is. But the bulk of within-person pair
distances (0.5 - 0.9) overlaps the impostor range measured in section 1.1 (0.55 - 0.75), which is the
signature of **misattributed samples** - other people enrolled under one name.

That inverts the value of clustering. Today a wrong sample is diluted into the centroid and mostly does
nothing. Promote it to its own template and it becomes a sharp, confident false-accept: whoever that voice
actually belongs to gets named as this person. Clustering a training set that has not been reviewed would
turn a quiet averaging problem into a loud misidentification one.

So the diagnostics come first, and they are far cheaper than section 6.2 assumed. Answering "which of my
samples do not belong" needs **no segment embeddings and no GPU at all** - the samples already carry
`vector(192)` embeddings, so it is pgvector arithmetic over data that exists:

- **Per sample, leave-one-out:** distance to the nearest other sample of the same person, and distance to the
  centroid of *the person's other samples* - "would the rest of this voiceprint recognise this?"
- **Per sample, a verdict** read off the already-calibrated thresholds rather than new invented ones: inside
  `IdentificationThreshold` of its siblings is **core**; inside `IdentificationConfirmBand` is a **variant**
  (a different recording condition); beyond it **sits alone** and is worth listening to.
- **Per person, cohesion**, and a directory-wide ranking - with 91 people, knowing *which* to look at first
  is most of the work.

Acting on a verdict needs nothing new: the training toggle and the clipped playback shipped in Phase 1.

### Phase 4 - Multi-template voiceprints

*(Was Phase 3. Unchanged in content; it now runs against a training set that has been reviewed.)*

Clustering; `ProfileVoiceprints`; best-template matching with the person-level margin; template
management UI; a re-scan against the improved gallery; the backup-restore template rebuild.

*Fixes the dilution in section 1.3.*

### Phase 5 - Segment scoring and the bench

The `segment-embed-jobs` stream; `Segments.VoiceEmbedding`; **segment-level** diagnostics (which lines
*inside* a sample are crosstalk, as opposed to which whole samples do not belong - that is Phase 3); the admin
bench with the leave-one-out threshold sweep.

*Delivers the original question 3, plus the test bench.*

---

## 11. Deployment surface

| Phase | Needs |
|---|---|
| 1 | Server redeploy. **API image rebuild** (ffmpeg) |
| 2 | Server redeploy |
| 3 | Server redeploy |
| 4 | Server redeploy **and worker image rebuild** (new stream + job handler) |

No desktop release at any phase - nothing touches `apps/desktop`.

---

## 12. Decisions taken, with the rejected alternatives

| Decision | Rejected alternative | Why |
|---|---|---|
| Auto-clustered templates, visible and nameable | Score against every sample ungrouped | One misattributed sample becomes a permanent false-accept magnet, and there is no way to name a condition |
| | Fully manual named voiceprints | Every person needs hand-filing; mis-filing degrades matching silently |
| | Auto-clustered but invisible | No way to see or fix a bad cluster, which defeats the bench |
| Confirmation band, margin, min-speech gate | Per-person threshold override | Per-person fiddling substitutes for fixing the template |
| Re-scan auto-applies confident, queues the rest | Everything is a proposal | 128 pending on day one and review labour forever |
| | Applies immediately, no queue | No way to judge borderline matches |
| | Continuous and automatic | Labels shift with no audit trail |
| Re-scan never revokes | Revoke on knob change | Strips correct labels because a slider moved |
| Lazy cached segment embeddings | Eager at transcription | GPU cost forever, spent on the 37% too short to score, plus a 57k-row backfill |
| | Transient | Every bench run pays full GPU cost; no comparison across runs |
| New `segment-embed-jobs` stream | Fold into `voiceprint-jobs` | Different callback shapes force a discriminated payload both sides must branch on |
| Server-clipped audio via ffmpeg in the API | Whole-file token | Real widening on large instances |
| | Worker-clipped and cached | Seconds of latency per first play, awkward when auditioning many short clips |
| | No cross-user audio | Assessment is much less useful without it, and Ken already spans both users |
| New `ManageVoiceprints` permission | Move people management to `ManagePlatform` | Directory hygiene should stay delegable without granting database restore |
