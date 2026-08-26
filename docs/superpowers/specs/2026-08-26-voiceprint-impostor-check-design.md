# Is this recording somebody else? - design

**Date:** 2026-08-26
**Status:** approved
**Follows:** `2026-08-25-speaker-identification-quality-design.md` (phases 1-3, shipped 0.250.0-0.255.0)
**Supersedes the immediate plan for:** that document's **Phase 4 - Multi-template voiceprints**, which is
deferred here with a stated re-decision point rather than cancelled.

## Why this instead of Phase 4

Phase 4 was to give a person several voiceprints - one per recording condition - so a phone, a car and a
meeting-room speaker each match. The training set was measured before building it.

Of 86 people with training data holding 153 samples, **61 have a single sample** and can never have
multiple templates. That leaves **25 people holding 92 samples**. Classifying each of those 92 by
whether its nearest neighbour is one of its own person's recordings or somebody else's:

| | count | what it is |
|---|---|---|
| Near its own siblings (<= 0.40) | 53 | healthy - one template is already right |
| Nearest its own, but distant (> 0.40) | **12** | genuine second-condition candidates - what Phase 4 helps |
| **Nearest a different person** | **27** | misattribution-shaped |

Of the 27, **9 sit within the accept distance (0.30)** of that other person and 13 within the confirm
band; 14 people are affected. Mean nearest-own is 0.413 against mean nearest-other 0.535, so the
populations overlap heavily.

**Clustering this set buys 12 improvements and creates 27 hazards, nine of them active false accepts.**
A misattributed recording today is a diluted nuisance inside an average. Promote it to a template of its
own and it becomes a confident match for the wrong person. Phase 4 as specced would make identification
measurably worse.

### The gap that made this invisible

The review surface shipped in 0.252.0 asks **"does this recording have company?"** - is the nearest
*sibling* beyond the suggest distance. It never asks **"is somebody else closer?"**. Of the 27 dangerous
samples it flags 22, misses 5, and **one reads as healthy**.

The second question is the one that separates a second microphone from a second human, which is exactly
the distinction Phase 3 established that distance-to-siblings cannot make. It costs one more comparison
over vectors that already exist.

## Design

### 1. The impostor question

`VoiceprintDiagnosis` sees only a person's own samples today, so it can only ask the first question. It
gains the other people's vectors and reports, per sample:

- `NearestImpostorDistance` - the closest sample belonging to **anyone else**.
- `NearestImpostorPersonId` / `Name` - who that was.

**Compared against other people's individual samples, not their centroids.** A centroid comparison is
more faithful to what identification does at runtime, but a person whose own voiceprint is already
diluted by a bad sample hides the very problem being hunted. Sample-level is more sensitive and points at
a *specific* recording that sounds the same - usually the one it was mixed up with.

Loaded in memory: 153 vectors of 192 floats is about 118 KB, and `DirectoryDiagnostics` already reads
every training sample this way. At ten thousand samples that becomes roughly 7.7 MB per request and
should move to a pgvector nearest-neighbour query with an index; it is not near that.

### 2. One new verdict, taking precedence

| verdict | when | tone |
|---|---|---|
| **Sounds more like someone else** | nearest impostor is closer than the nearest sibling | red |
| Sounds unlike their others | nearest sibling beyond the suggest distance | amber |
| A different recording condition | nearest sibling inside the confirm band | blue |
| Matches their other recordings | nearest sibling inside the accept distance | green |
| Nothing to compare it with | no sibling | grey |

The new verdict takes precedence because it is strictly more serious: if a sibling were closer, the
question could not arise. It replaces `Alone` for 22 of the 27 and catches the 5 that no verdict caught.

**It names the other person**, which turns a diagnosis into a one-click fix - reassignment shipped in
0.254.0, so the control to act on it is already on the row.

### 3. Confirmation is a separate assertion

`VoiceSample.ConfirmedAt` and `ConfirmedByUserId`, with a row control: **Confirmed as this person**.

Deliberately **not** the same as *Trains the voiceprint*, and both are kept:

- **Trains** - should this audio feed the biometric? A recording can be genuinely them and still be too
  noisy to learn from.
- **Confirmed** - is this the right person? An assertion about identity, not about quality.

Revocable. A confirmed row keeps showing its verdict, muted: the distance is still what it is, the user
has simply said they are content with it.

**No bulk confirm**, including no "confirm everything rated healthy". The entire reason for the gate is
that distance cannot tell the two cases apart and only listening can; a button that confirms unheard
audio would reintroduce exactly the failure being fixed. The data makes that concrete - one sample reads
as healthy today while sitting closer to somebody else.

Distinct from `SpeakerIdentityDecision`, which records accepting or rejecting a *suggested* identity.
This records a human vouching for a link that already exists.

**What it does today:** drops the row out of "worth checking", and the person out of the directory's
needs-review count. **What it is for:** Phase 4 gates template-seeding on it.

### 4. The queue reorders and shrinks

"Sounds more like someone else" outranks "sounds unlike their others" in both the per-person ordering and
the directory ranking, and confirmed recordings drop out of both. The list shrinks as it is worked, and
its top is always the most dangerous thing left.

### 5. Re-measuring is the diagnostics themselves

No separate report or admin bench. The per-person and directory diagnostics **are** the measurement
surface; the counts above can be re-derived from them once the queue has been worked.

**The re-decision point for Phase 4:** how many samples are still "nearest their own person but beyond
the suggest distance" after the misattributions are gone. Today that is 12, against 27 hazards. If
cleaning leaves that figure materially larger than 12, multi-template earns its table, template
management UI, best-of matching, re-scan and backup-restore rebuild. If it stays at a dozen, it does not.

## Testing

The classification is a pure function over vectors, so its own tests need no database: a sample whose
nearest impostor beats its nearest sibling, one where the sibling wins, one with no sibling at all, one
with no impostor at all (a directory of one person), and the boundary where the two distances are equal.
Mutation-verify each.

What those cannot cover, and so needs the Testcontainers layer:

- The diagnostics endpoint returns the impostor's identity, not just a distance.
- Confirmation round-trips as a `timestamptz` (Npgsql rejects a non-zero-offset `DateTimeOffset`, and the
  in-memory provider will not catch it).
- A confirmed sample leaves the directory ranking, and an unconfirmed one returns to it.
- The ordering puts an impostor-flagged sample above an `Alone` one.

Web tests cover the new verdict's wording and precedence, that the other person is named, the confirm
control and its revocation, and that confirmation does not change the training tick.

`jsdom` computes no geometry, so nothing here asserts how the extra figure sits in an already-dense row.
That is verified in the running app.

## Delivery

One PR. Server redeploy only: two nullable columns, additive and forward-restore-safe, so **no
`CurrentFormat` bump**. No worker or GPU involvement anywhere - it is arithmetic over embeddings that
already exist.

## Out of scope

- **Multi-template voiceprints (Phase 4).** Deferred, with the re-decision point stated above.
- **Segment scoring and the threshold bench (Phase 5).** Unchanged; still waits on a reviewed set.
- **Automatically acting on an impostor finding.** The finding names the likely person, and the existing
  reassign control does the rest. Applying it automatically would be the same mistake as clustering
  unreviewed data: confident action on evidence that cannot distinguish the two cases.
- **A pgvector index on `ProfileContributions.Embedding`.** Warranted at roughly two orders of magnitude
  more samples than the instance holds today.
