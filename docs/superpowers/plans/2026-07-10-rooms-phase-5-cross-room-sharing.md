# Rooms Phase 5 - Cross-Room Sharing & RAG Re-scope

> **For agentic workers:** Execute task-by-task (executing-plans). Keep the accumulation branch `feat/rooms-phase-2c-plus` (PR #264) green at every commit.

**Goal:** Manual cross-room sharing (Share to another room / Remove from room), the Rooms + Recorded-by lines on the recording Overview, the delete confirmation naming the shared rooms a recording will vanish from, and the RAG/keyword search re-scoped so chat + MCP tools span every room the caller belongs to.

**Architecture:** `RoomScope` grows a "rooms a recording is placed in" query, a single-placement unshare, and a "room ids the caller belongs to" set. `RecordingDetailDto` gains `recordedBy` + `rooms`. Two endpoints - share and unshare - enforce the permission model (`ShareOut` in the source room + `CreateRecording` in the target to share; `RemoveOthersRecordings` or being the recorder to unshare; never the main room). `TranscriptSearch`'s five user-scoped SQL arms become a `RoomRecordings` semi-join over the caller's room ids. The web detail page reads the current room from `useRoom()`, renders the two Overview lines, and adds Share / Remove-from-room to the shared `recordingMenu`.

**Standing constraints:** Postgres-only model config behind `Database.IsNpgsql()`. `[Flags]` DTO fields as `int`. No em/en dashes in user-facing text. One release on PR #264 - extend `0.119.0`. `TranscriptChunk.UserId` stays (the drop is the deferred Task 8 follow-up); the search re-scope uses the `RoomRecordings` semi-join, not that column.

---

## Verified anchors

- **`RecordingsController.Get`** (`RecordingsController.cs:135-192`): loads highest-version transcription; ownership gate `r.UserId == UserId` (:148); builds `RecordingDetailDto` (ApiDtos.cs:188-218) - **no** recorded-by, **no** rooms. `_rooms` is injected.
- **`RoomScope`** has `RoomsForUserAsync`, `RecordingsIn(roomId)` (unused), `ShareIntoRoomAsync` (idempotent, Shared-only), `DeleteRoomAsync`, `PermissionsAsync`, `IsMemberAsync`. **Missing:** rooms-for-a-recording, single-placement unshare, room-ids-for-user.
- **`TranscriptSearch`** (`TranscriptSearch.cs`): 5 user-scoped arms - lexical `r."UserId" = @userId` (:123), **semantic** `c."UserId" = @userId` (:179, keys on `TranscriptChunks`), list (:224), count (:295), talk-time (:332). `CurrentVersion` guard (:75-76). Callers: the 7 tools in `src/Diariz.Api/Tools/` (chat + MCP), all pass `ctx.UserId`. DI at `Program.cs:296`.
- **`RecordingDetail.tsx`**: Overview `<dl>` metadata rows at :939-964 (insert Rooms + Recorded-by after :951); `menuActions = recordingMenu({...})` at :872-913 (`onMove` → `setMoving(true)`); **does not** use `useRoom()` today. Web type `RecordingDetail` (types.ts:295-330); `api.getRecording` (api.ts:283-286, no roomId).
- **`recordingMenu.ts`**: spread-guarded builder; add optional `onShare?`/`onRemoveFromRoom?` near `moveToSection` (:78). `KebabAction {label,onClick,danger?,disabled?}`. Two call sites: `RecordingDetail.tsx:872`, `RecordingsPanel.tsx:1217`.
- **`MoveToSectionModal.tsx`**: clone target for the room picker (but the shared-room link is **always ungrouped for now**, so the Share modal is just a room picker - no folder step).

---

## Task 1: `RoomScope` - rooms-for-recording, unshare, room-ids-for-user

**Files:** `src/Diariz.Api/Services/RoomScope.cs` (+ interface); `tests/Diariz.Api.Tests/RoomScopeSharingTests.cs`

Add (TDD):
```csharp
public record RecordingRoomPlacement(Guid RoomId, string Name, RoomKind Kind, string? Icon, string? Color, bool IsMainRoom);

// The rooms a recording is placed in (main first, then shared by name).
Task<IReadOnlyList<RecordingRoomPlacement>> RoomsForRecordingAsync(Guid recordingId, CancellationToken ct = default);

// Remove a single non-main placement (unshare). False if no such row, or if it is the main placement.
Task<bool> RemoveFromRoomAsync(Guid recordingId, Guid roomId, CancellationToken ct = default);

// The ids of every room the caller belongs to (personal + shared). Small set - fed to the search semi-join.
Task<IReadOnlyList<Guid>> RoomIdsForUserAsync(Guid userId, CancellationToken ct = default);
```
- `RemoveFromRoomAsync`: load the `RoomRecording`; if null or `IsMainRoom` → false; else remove + save → true.
- `RoomIdsForUserAsync`: reuse `RoomsForUserAsync` and project the ids (or a lighter query).
- Tests: placements listed main-first; unshare drops a shared row but refuses the main row (recording survives); room-ids includes personal + a shared room the user is in.

Commit: `feat(api): RoomScope - a recording's rooms, single-placement unshare, and a caller's room ids`.

---

## Task 2: Recording detail exposes recorded-by + its rooms

**Files:** `ApiDtos.cs` (`RecordingDetailDto` gains `Guid RecordedByUserId`, `string? RecordedByName`, `IReadOnlyList<RecordingRoomDto> Rooms`), `RecordingsController.Get`, web `types.ts` + Overview render; tests.

- `RecordingRoomDto(Guid Id, string Name, string? Icon, string? Color, bool IsMain)`.
- `Get` resolves recorded-by (the `Recording.UserId` → the user's display name) and `_rooms.RoomsForRecordingAsync(id)`, filtered to the rooms the **caller** can see (a member of) - a caller viewing a shared recording shouldn't see rooms they aren't in. Main room first.
- Web: add a **Rooms** `<dt>/<dd>` row (icon chips + names, main first) and a **Recorded by** row to the Overview `<dl>` (~RecordingDetail.tsx:951). New i18n keys in `workspace`.

Commit: `feat: recording detail shows who recorded it and which rooms it is in`.

---

## Task 3: share + unshare endpoints

**Files:** `RecordingsController.cs` (share), a new `RoomRecordingsController` or a route on `RecordingsController` (unshare); `ApiDtos.cs` (share request); `RoomScope` (reuse `ShareIntoRoomAsync` / `RemoveFromRoomAsync`); tests.

- `POST /api/recordings/{id}/share` body `ShareRecordingRequest(Guid FromRoomId, Guid ToRoomId)`:
  - The recording must be placed in `FromRoomId` (else 404) and the caller must hold `ShareOut` there.
  - The caller must hold `CreateRecording` in `ToRoomId` (else 403), and `ToRoomId` must be a Shared room (`ShareIntoRoomAsync` returns false otherwise → 400).
  - Shares ungrouped (`sectionId: null`). Idempotent.
- `DELETE /api/rooms/{roomId}/recordings/{id}` (unshare):
  - The caller must be a member of `roomId`, and hold `RemoveOthersRecordings` there **or** be the recorder (`Recording.UserId == caller`).
  - `RemoveFromRoomAsync` refuses the main room → 400 ("delete from its home room instead").
- Tests (unit, in-memory): share adds a shared placement; sharing to a room without `CreateRecording` is 403; unshare removes a shared placement; unshare of the main room is refused; unshare by a non-privileged non-recorder is 403.

Commit: `feat(api): share a recording into another room and unshare it`.

---

## Task 4: web Share / Remove-from-room + delete confirmation

**Files:** `recordingMenu.ts` (+ handlers), `RecordingDetail.tsx` (wire `useRoom()`, handlers, modals, delete-confirm), a new `ShareToRoomModal.tsx`, `api.ts` (`shareRecordingToRoom`, `removeRecordingFromRoom`), locales; `recordingMenu.test.ts`.

- `recordingMenu`: add `onShare?`/`onRemoveFromRoom?` spread-guarded near `moveToSection`. **Remove from room** shows only when the current room is a shared room the recording is placed in; **Delete** is hidden when the current room is not the recording's main room (pass a `canDelete` flag / gate `onDelete`).
- `RecordingDetail`: read `useRoom().currentRoom`; `onShare` opens `ShareToRoomModal` (lists rooms where the caller holds `CreateRecording`, excluding the ones it is already in), which POSTs share. `onRemoveFromRoom` calls `removeRecordingFromRoom(currentRoom.id, id)` then navigates back to the list. The Delete confirmation lists the shared rooms from `rec.rooms` so the recorder knows what they are destroying.
- Tests: `recordingMenu` includes Share; hides Delete outside the main room; shows Remove-from-room only in a shared room.

Commit: `feat(web): Share to another room, Remove from room, and a delete that names shared rooms`.

---

## Task 5: search + chat/MCP tools span the caller's rooms (RAG re-scope)

**Files:** `TranscriptSearch.cs` (inject `IRoomScope`; replace the 5 user-scoped arms with a `RoomRecordings` semi-join over `RoomIdsForUserAsync`), its construction sites; integration tests (Postgres-only).

- At the top of each method resolve `var roomIds = (await _rooms.RoomIdsForUserAsync(userId, ct)).ToArray();` and pass `@roomIds uuid[]`.
- Replace `r."UserId" = @userId` (lexical/list/count/talk-time) with
  `EXISTS (SELECT 1 FROM "RoomRecordings" rr WHERE rr."RecordingId" = r."Id" AND rr."RoomId" = ANY(@roomIds))`.
- Replace the **semantic** arm `c."UserId" = @userId` with
  `EXISTS (SELECT 1 FROM "RoomRecordings" rr WHERE rr."RecordingId" = c."RecordingId" AND rr."RoomId" = ANY(@roomIds))`.
- The `RoomRecording(RoomId)` index already exists; the spec flags this as a **measured** risk - note in the PR that we did not benchmark a large corpus, and the denormalise-`RoomId`-onto-chunk fallback remains open if it regresses.
- Integration tests: a recording shared into a room the searcher belongs to **is** found; a recording in a room they are **not** in is **not** found; the recorder still finds their own (personal-room) recordings. Seed `RoomRecording` placements (not just `Recording.UserId`).

Commit: `feat(api): chat and MCP search span every room the caller belongs to`.

---

## Task 6: docs, release entry

**Files:** synopsis (Phase 5 done: sharing endpoints, the Overview lines, the search semi-join + its measurement caveat), `docs/Data_Schema.md` if any index note is needed (none - reuse existing), README/features/CAPABILITIES (cross-room sharing is now user-visible), `releases.ts` (extend `0.119.0`).

Commit: `docs: cross-room sharing + room-scoped search (Phase 5)`. Push to PR #264.

---

## Self-review

- **Spec coverage:** share/unshare endpoints (T3) + UI (T4), Rooms/Recorded-by lines (T2), delete-confirm naming shared rooms (T4), RAG semi-join + measurement caveat (T5). Membership is the read gate (404 not 403 for non-members).
- **Permission model:** share = `ShareOut` (source) + `CreateRecording` (target); unshare = `RemoveOthersRecordings` or recorder, never main; delete = recorder + main room only.
- **Greenness:** T5 changes `ITranscriptSearch` construction - fix all sites; every task ends green.
- **One release:** extend `0.119.0`.
