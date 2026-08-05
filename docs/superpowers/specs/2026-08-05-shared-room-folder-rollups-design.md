# Shared-Room Folder Roll-Ups

**Date:** 2026-08-05
**Status:** Design, implemented in the same PR

## 1. Goal

A folder in a **shared room** produces an empty summary and empty minutes, however many recordings it
contains. Fix that, so a folder rolls up the recordings it actually holds regardless of which room it
lives in.

## 2. The bug

`SectionSummaryProcessor.IncludedRecordingsAsync` resolves a folder's recordings in two steps. The
first step is right; the second is not:

```csharp
var allIds = await SectionTree.SubtreeIdsAsync(db, section.RoomId, section.Id, default);  // correct
return await (
    from p in db.RoomRecordings
    join rm in db.Rooms on p.RoomId equals rm.Id
    join r in db.Recordings on p.RecordingId equals r.Id
    where rm.OwnerUserId == section.UserId && rm.Kind == RoomKind.Personal   // <-- the bug
          && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
    ...
```

`allIds` holds section ids **from the folder's own room**. The placement join only ever looks at
placements in the **section owner's personal room**. `RoomRecording.SectionId` is the folder *within
that room*, so for a shared-room folder the two sets are drawn from different rooms and their
intersection is always empty. Not "sometimes wrong" - structurally empty, every time.

Both folder-level LLM features run through this one helper, so both are affected:

| Caller | Symptom |
| --- | --- |
| `SectionSummaryProcessor.ProcessAsync` | Folder summary comes back empty |
| `SectionMinutesProcessor` (`:44`) | Folder minutes come back empty |

**It is visible, not silent.** `SectionPageController` scopes placements by `p.RoomId == roomId`
(`:80`, `:119`, `:142`, `:166`), so the same folder page reports a non-zero recording count beside a
summary of nothing. And the path is reachable: generating a folder summary in a shared room needs only
`ManageContents`, which `SectionPageControllerTests.GenerateSummary_in_a_shared_room_needs_ManageContents`
already exercises.

## 3. Why it was not fixed earlier

It was found during PR #446 and deliberately left alone. That PR was a behaviour-preserving refactor -
its whole value was that it could not change what users saw - and this is a real behaviour change
needing its own tests and release note. Fixing it there would have destroyed the property that made
that PR safe to merge.

## 4. The fix

Replace the personal-room join with the folder's own room, matching how every other roll-up site
already resolves placements:

```csharp
where p.RoomId == section.RoomId
      && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
```

The `Rooms` join disappears with it - nothing else used it.

**This is a no-op for personal-room folders**, which is what makes it safe. For a folder in a user's
personal room, `section.RoomId` *is* that personal room's id, so `p.RoomId == section.RoomId` selects
exactly the placements the old two-predicate form selected. The behaviour only changes where the old
form was already returning nothing.

**Why `section.UserId` was the wrong anchor at all:** in a shared room, `Section.UserId` is whoever
happened to create the folder. Resolving a shared folder's contents through that person's private
library is not a near-miss, it is unrelated data.

## 5. The test debt this forces into the open

`SectionSummaryProcessorTests.SeedSection` creates sections with **no `RoomId`** (defaulting to
`Guid.Empty`), while its `SeedRecording` places recordings into the user's **real** personal room via
`RoomScope.PlaceInMainRoomAsync`. Today those disagree harmlessly: the folder walk matches
`Guid.Empty` against itself, and the placement join independently finds the real room.

Once placements are scoped by `section.RoomId`, the two halves must agree, and four existing tests
break. That is the fixture being wrong, not the fix - a reviewer flagged this exact debt during PR #446
and noted it would matter "if `RoomId` ever becomes load-bearing in those tests". It just did.

So `SeedSection` gains a real room id: the user's personal room, the same one `PlaceInMainRoomAsync`
files into. The tests then assert against a coherent world rather than two halves that never meet.

## 6. Non-goals

- **Widening any other roll-up.** The four other sites already scope by room correctly.
- **Changing who may generate a folder summary.** The `ManageContents` gate is unchanged; this only
  fixes what the job reads once it runs.
- **Backfilling anything.** No stored data is wrong - the query was. Previously-generated empty
  summaries can be regenerated from the folder page.

## 7. Testing

- **Unit, the bug itself:** a folder in a **shared** room with a recording placed in that room. The
  summary run must reach it. This fails before the change (zero recordings summarised) and passes
  after - the red-first proof that the fix does what it claims.
- **Unit, the no-op claim:** the four existing personal-room tests must still pass, once their fixture
  seeds a real room. They are the regression net for section 4's equivalence argument.
- **Minutes:** `SectionMinutesProcessor` shares the helper, so a matching shared-room test there proves
  the second caller is fixed too rather than assuming it.

Tests drive the public `ProcessAsync` seam with the fakes in `Diariz.Api.TestSupport`, because
`IncludedRecordingsAsync` is `internal` and this repository has no `InternalsVisibleTo` - it tests
through public seams rather than widening visibility.

## 8. Release

A fix, so **Build +1**: `0.180.0` -> `0.180.1`, across `version.json` and its four mirrors, with a
`RELEASES[0]` entry. No schema change, no migration, no `MaintenanceController.CurrentFormat` bump.

Deployment surface: **server redeploy only.**
