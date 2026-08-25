# Voiceprint review surface - design

**Date:** 2026-08-25
**Status:** approved
**Follows:** `2026-08-25-speaker-identification-quality-design.md` (phases 1-3, shipped as 0.250.0-0.252.0)

## Why

Four reports from the first live use of the 0.252.0 people surface:

1. The two hero panels ("Voiceprints worth checking", "Possible duplicates") push the person card
   off screen, and a voiceprint warning has no dismiss when a duplicate does.
2. The Diagnostics tab is not actionable - no play, no remove, no reassign.
3. On the Voiceprint tab, "Play voice" is disabled rather than hidden when unavailable, and it still
   reads "Stop" - and keeps playing - after the segment list is collapsed.
4. The Diagnostics wording contradicts itself: the header says "5 recordings resemble none of the
   others" above a list whose rows say "Matches the others".

Investigating (2) found a defect underneath it, described next. That defect, not a missing button,
is the reason the tab could not be acted on.

## The finding: training samples outlive their speaker link

`SpeakerAssignment.Unassign` clears `Speaker.PersonId` and `AssignAsync` repoints it, but neither
touches the `VoiceSample` already recorded for the old person. The sample keeps training that
person's voiceprint, forever, invisibly.

Measured on the live instance: of 167 training samples, **six** are in this state, spread across six
different people.

| how the link was lost | samples |
|---|---|
| speaker reassigned to a different person | 3 |
| speaker unassigned entirely | 2 |
| speaker marked as overlapping speech | 1 |

The first row is the serious one: three voiceprints are being trained on audio the user has since
labelled as somebody else, so two different people are taught the same voice.

This is also the direct cause of report (2). The Voiceprint tab lists **linked speakers**; the
Diagnostics tab lists **samples**. For the worst-ranked person in the directory, the top diagnostics
row - "resembles none of the others" - is one of these six, so it has no counterpart on the
Voiceprint tab. It could not be played or removed because it was not there.

## Design

### 1. One rule for what trains a voiceprint

> A voice sample trains a person's voiceprint only while its speaker still says it is that person.

Expressed as a pure predicate rather than stored state, so it cannot drift and no future unassign or
reassign can create a seventh orphan:

```csharp
/// A sample trains a voiceprint only while its speaker still says it is that person. Stored state
/// would need every assignment path to remember to update it; a rule needs none of them to.
public static bool Trains(VoiceSample sample, Speaker? speaker) =>
    sample.ExcludedAt is null
    && speaker is not null
    && speaker.PersonId == sample.PersonId
    && !speaker.IsMultiSpeaker;
```

Applied at every point that decides what a voiceprint is made of:

- `PeopleDirectory.RecomputeVoiceprintAsync` - the centroid and `SampleCount`.
- `PeopleController.Diagnostics` - the training set that outliers are measured against.
- `PeopleController.DirectoryDiagnostics` - the health ranking.

`PersonAttributions.Build` already satisfies the rule structurally (it iterates linked speakers and
skips multi-speaker ones), so its behaviour does not change - but it routes through the same helper
so a future edit cannot quietly diverge.

**Rebuilding the six.** The centroids stored today were computed from the orphans and stay wrong
until something recomputes them. A convergent startup pass - find people holding a sample the rule
rejects, call `RecomputeVoiceprintAsync` for each - fixes them on next boot. It is idempotent by
construction (once converged it finds nothing), so it needs no run-once marker and cannot re-apply
itself the way a `Seeder` backfill would.

It deliberately does **not** re-derive the centroid in SQL. A migration computing the same value a
second way would agree with the C# path by luck; there is one derivation, and the pass calls it.

### 2. Orphans stay visible

Dropping six samples out of six voiceprints with no trace would be its own bug. The attributions
endpoint gains them as rows, flagged:

- `PersonAttributionDto.StillLinked` - false for a sample whose speaker no longer points here.
- `PersonAttributionDto.CanReassign` - true only when the caller **owns** the recording.

`CanReassign` is separate from the existing `CanAccessRecording` on purpose. `ManageVoiceprints`
grants listening to a segment for assessment; it does not grant editing someone else's transcript,
and `RecordingsController.AssignSpeaker` enforces ownership regardless.

### 3. Directory: banners become row commentary

Both hero panels are removed.

- A person with something to say gains a **third line** under their name, amber, present only when
  there is a warning: `3 of 8 recordings sound different`, `Possible duplicate`.
- A **Needs review** filter chip joins All / Internal / External / Has voiceprint. This is the real
  replacement for the ranking - scanning a long directory for amber is not a way to find anything.
- **Dismiss** sits on that line and lasts the sitting, matching how duplicate dismissal already
  behaves. Keyed on the person.
- The merge prompt moves to a slim strip above the editor panel, shown when the selected person is
  in a live duplicate group, carrying "Review and merge" and "Dismiss". The notice follows the
  person you opened rather than sitting above the whole directory.

### 4. Voiceprint tab absorbs Diagnostics

The third tab is deleted. `PersonVoiceprintTab` additionally reads `api.getPersonDiagnostics` and
joins on **`speakerId`** - present on both DTOs, unlike `voiceSampleId` which is null for an
attribution that trains nothing. No API change is needed for the join.

- **Order:** outliers first, then the server's existing order by recording name.
- **Header:** describes the list beneath it, so the count and the rows cannot contradict each other.
  `Trained on 13 of 14 recordings. 5 sound unlike the rest - worth a listen.` When there is one
  sample: `Only one recording, so there is nothing to compare it with.`
- **Filter:** an "only ones worth checking" toggle, shown only when at least one qualifies.
- **Numbers become similarity, not distance.** `1 - distance`, clamped at 0, labelled `closest
  match` and `match to the rest`. Today the worst row in the directory displays the largest and most
  reassuring-looking number on the screen: the worst observed outlier reads `closest other: 82%` and
  becomes `closest match 18%`.
- **Verdicts stay in words**, keyed to the same thresholds as before:

  | verdict | wording | tone |
  |---|---|---|
  | `Core` | Matches their other recordings | green |
  | `Variant` | A different recording condition | blue |
  | `Alone` | Sounds unlike their others | amber |
  | `Only` | Nothing to compare it with | grey |

  An orphan row shows `No longer linked to this person` instead, and does not count toward the
  header's "sound unlike the rest" figure - it is not a judgement about the voice.

### 5. Row actions

- **Not this person...** - a person picker, plus *Nobody* to unlink, calling
  `api.assignSpeaker(recordingId, speakerLabel, personId | null)`. Rendered only when
  `canReassign`.
- **Play voice** is hidden, not disabled, when the segments are not loaded. Collapsing the segment
  list stops any playback that row owns.
- The existing "Trains the voiceprint" checkbox and per-segment play are unchanged.

## Testing

The rule is a pure function, so its own tests need no database: a sample whose speaker is unlinked,
one whose speaker points at a different person, one on a multi-speaker, one excluded by hand, and
the healthy case. Verify each fails when the corresponding clause is deleted.

What those cannot cover, and so needs the Testcontainers layer:

- The startup rebuild pass actually changes a stored centroid, and is a no-op on a second run.
- `RecomputeVoiceprintAsync` and both diagnostics endpoints agree about which samples count - the
  failure being fixed here is precisely two surfaces disagreeing.
- An orphan row appears in the attributions payload with `stillLinked: false`.
- `CanReassign` is false for a recording the caller does not own but can access via
  `ManageVoiceprints`, and the underlying `AssignSpeaker` still refuses it.

Web tests cover: the merged list ordering and header text, the similarity flip (a `0.82` distance
renders `18`), the orphan row's wording, the play button's absence rather than its disabled state,
playback stopping on collapse, the directory warning lines, the Needs review filter, and dismissal.

`jsdom` computes no geometry, so nothing here asserts that the panels stopped covering the card -
that is verified in the running app.

## Delivery

Three PRs, each shippable alone. Versions are set per PR at the time.

1. **The rule and the rebuild** (fix). Server only. Closes the orphan issue.
2. **The merged tab and the directory** (feature). Reports 1, 3 and 4. Depends on 1 for orphan rows.
3. **Reassign** (feature). Report 2's last piece; needs `CanReassign` from PR 1.

Deployment surface: all three are **server redeploy** only. Nothing touches the desktop shell.

## Out of scope

- **Persistent dismissal.** Chosen as session-only. The warning returns each time the modal is
  reopened; with the hero panel gone it is a quiet line rather than a blocking panel.
- **Phase 4 (multi-template voiceprints) and Phase 5 (segment scoring and the threshold bench)**
  from the parent design. Both still wait on a reviewed training set, and this work is what makes
  reviewing one possible.
- **Changing `Unassign` to exclude the sample.** The rule makes it unnecessary: an unassigned
  speaker's sample stops training without any assignment path having to remember to say so.
