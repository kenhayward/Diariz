# Rooms Phase 4 - Shared Rooms

> **For agentic workers:** Execute task-by-task (executing-plans). Keep the accumulation branch `feat/rooms-phase-2c-plus` (PR #264) green at every commit. This is the largest phase; each task is an independently-green increment.

**Goal:** Make shared rooms real - create/rename/delete rooms and edit their membership (Manage Rooms modal), record into a shared room (a second placement alongside the always-personal main), orphan a user's personal room and sweep their room memberships on delete, and complete the deferred `UserId`->`RoomId` migration for folders/voiceprints/chats/meeting types.

**Architecture:** `RoomScope` grows the room-lifecycle, membership, and share/unshare operations; a write-enabled `RoomsController` exposes them (gated by the `ManageRooms` platform permission, with the Personal room immutable). The web gains a shared `IconColorPicker` and a Manage Rooms modal, reached from the room switcher. Upload learns a `roomId` and writes the two-placement transaction. Finally the per-user queries for the four room-scoped entities flip to `RoomScope`, and a migration adds the Rooms FKs and drops the now-dead `UserId` columns.

**Tech stack:** ASP.NET Core (.NET 10) + EF Core + Postgres; React 19 + TS + React Router + React Query; xUnit / Testcontainers / vitest. TDD throughout.

**Standing constraints:** One-way data moves live in EF migrations, never the seeder. New Postgres-only model config sits behind `Database.IsNpgsql()`. `[Flags]` permission DTOs cross the wire as `int` (the global string-enum converter would emit `"A, B"`). No em/en dashes in user-facing text. This whole phase is part of PR #264's single release - extend the `0.119.0` entry (it is already a functional enhancement; keep the version unless the user says otherwise). Personal rooms are immutable + private; enforce server-side.

---

## Verified anchors (from exploration)

- **Rooms entities** (`src/Diariz.Domain/Entities/`): `Room {Id, Name, Description, Icon?, Color?, Kind, OwnerUserId?, CreatedAt, Members}`; `RoomMember {RoomId, PrincipalType (User=0/Group=1), PrincipalId (no FK), Permissions (RoomPermission)}` composite PK `{RoomId, PrincipalType, PrincipalId}`; `RoomRecording {RoomId, RecordingId, IsMainRoom, SectionId?, SharedByUserId?, SharedAt?}` composite PK `{RoomId, RecordingId}`.
- **DbContext Rooms config** (`DiarizDbContext.cs` ~70-139): `Room.Owner` FK **SetNull**; `RoomMember.Room` FK **Cascade** + index `{PrincipalType, PrincipalId}`; `RoomRecording` FKs Cascade/Cascade/**SetNull(Section)** + index `{RoomId, SectionId}`; Npgsql-only: filtered-unique `Room.OwnerUserId WHERE NOT NULL`, filtered-unique `Room.Name WHERE Kind=1`, filtered-unique `RoomRecording.RecordingId WHERE IsMainRoom`, check `CK_RoomRecordings_MainRoomHasNoSharer`.
- **RoomScope** (`src/Diariz.Api/Services/RoomScope.cs`): has `PersonalRoomIdAsync`, `RoomsForUserAsync`, `PermissionsAsync`, `IsMemberAsync`, `RequireAsync`, `RecordingsIn`, `PlaceInMainRoomAsync`, `SectionIdAsync`, `SetSectionAsync`. Personal-room owner implicitly holds `AllPermissions` (const ~53). **Missing:** room create/update/delete, member add/remove/set-perms, share-into/unshare.
- **RoomsController** (`src/Diariz.Api/Controllers/RoomsController.cs`): only `GET` today.
- **Groups pattern to mirror** (`GroupsController.cs`): `[Authorize(Policy="ManageUsers")]`, `GroupInput`/`GroupDto` (`Permissions` as int), create/update/delete + `PUT/DELETE {id}/members/{userId}`. The equivalent room policy is **`ManageRooms`** (confirm the policy name in `Program.cs`).
- **AdminUsersController.Delete** (`AdminUsersController.cs:192-201`): `await _users.DeleteAsync(user)` - relies on DB cascades. `Room.OwnerUserId` already SetNull-orphans. **Phase 4 must** sweep `RoomMembers WHERE PrincipalType=User AND PrincipalId=id` here, and add an "also delete their recordings" opt-in.
- **IconColorPicker source** (`ManageMeetingTypesModal.tsx:287-313`): a native `<input type="color">` + the 16-key `MEETING_TYPE_ICONS` from `MeetingTypeIcon.tsx:7-10`, rendered via `<MeetingTypeIcon icon color size>`. No discrete colour palette exists.
- **ManageUsersModal** (`ManageUsersModal.tsx:97-119`): `tab: "users"|"groups"`, overlay pattern, `GroupsTab` reused. Manage Rooms is a **separate modal** off the room switcher, not a tab here (spec).
- **Upload** (`RecordingsController.cs:198-256`): `Upload(audio, title, durationMs, source, sectionId)` - no `roomId`. Placement via `PlaceInMainRoomAsync`. Web `api.upload(blob, title, durationMs, source, sectionId)`.
- **UserId-scoped queries to flip** (Task 8): Section - `SectionPageController.cs:48,61,148,171,191,196,220,237`, `SectionAttachmentsController.cs:44`, `RecordingsController.cs:110,923`; SpeakerProfile - `SpeakerProfilesController.cs:34,44,100,121,163,188,189,213,227`; ChatSession - `ChatController.cs:233,241,275,292,321`; MeetingType - `MeetingTypesController.cs:43,134`. Recordings keep `UserId` (= "recorded by").

---

## Task 1: `RoomScope` room-lifecycle + membership operations

**Files:** `src/Diariz.Api/Services/RoomScope.cs` (+ interface); `tests/Diariz.Api.Tests/RoomScopeLifecycleTests.cs`

Add to `IRoomScope` / `RoomScope` (all TDD - one failing test per behaviour first):

```csharp
Task<Guid> CreateSharedRoomAsync(string name, string? description, string? icon, string? color, CancellationToken ct = default);
Task<bool> UpdateRoomAsync(Guid roomId, string name, string? description, string? icon, string? color, CancellationToken ct = default);
Task<bool> DeleteRoomAsync(Guid roomId, CancellationToken ct = default);
Task<bool> SetMemberAsync(Guid roomId, RoomPrincipalType type, Guid principalId, RoomPermission permissions, CancellationToken ct = default); // upsert
Task<bool> RemoveMemberAsync(Guid roomId, RoomPrincipalType type, Guid principalId, CancellationToken ct = default);
```

Rules enforced in `RoomScope` (tested):
- Create only ever makes `Kind = Shared`; a Personal room is minted only by `PersonalRoomIdAsync`.
- Update/Delete/SetMember/RemoveMember **refuse a Personal room** (return false) - personal rooms are immutable and memberless. Test: updating a personal room is a no-op false.
- Delete removes the room (cascades members + placements via the existing FKs). Because no shared room is a main room, this only unshares recordings, never destroys them - assert a shared placement's recording still exists after its room is deleted.
- `SetMemberAsync` upserts the `(RoomId, type, principalId)` row's `Permissions`.

Commit: `feat(api): RoomScope create/update/delete rooms and edit membership`.

---

## Task 2: write-enabled `RoomsController`

**Files:** `src/Diariz.Api/Controllers/RoomsController.cs`; `src/Diariz.Api/Contracts/ApiDtos.cs` (room write DTOs + a `RoomDetailDto` with members); `tests/Diariz.Api.Tests/RoomsControllerTests.cs` (extend)

- Confirm the platform policy name for room management in `Program.cs` (expect `"ManageRooms"`). Gate writes with `[Authorize(Policy = "ManageRooms")]` at the action level (GET stays open to any member).
- `POST /api/rooms` (create shared), `PUT /api/rooms/{id}` (rename/describe/icon/colour), `DELETE /api/rooms/{id}` (unshare-delete), `PUT /api/rooms/{id}/members` (upsert a member with a permission grid), `DELETE /api/rooms/{id}/members/{type}/{principalId}`.
- `GET /api/rooms/{id}` returns a `RoomDetailDto` (room + its members: principal type/id + permission bitmask as **int**), for the Manage Rooms editor. Only members / `ManageRooms` holders may read it.
- Personal room writes -> 400/403 (mirror `RoomScope` returning false). Name required + unique among shared (409 on dup, like groups).

DTOs (mirror `GroupDto`/`GroupInput`, permissions as **int**):
```csharp
public record RoomInput(string Name, string? Description, string? Icon, string? Color);
public record RoomMemberInput(RoomPrincipalType PrincipalType, Guid PrincipalId, int Permissions);
public record RoomMemberDto(RoomPrincipalType PrincipalType, Guid PrincipalId, int Permissions);
public record RoomDetailDto(Guid Id, string Name, string? Description, string? Icon, string? Color, IReadOnlyList<RoomMemberDto> Members);
```
Commit: `feat(api): create, edit and delete shared rooms + membership over HTTP`.

---

## Task 3: user-delete orphans the personal room and sweeps memberships

> **Decision (2026-07-10):** the user chose to **keep today's behaviour** - deleting a user still deletes their
> recordings. So this task is **just the RoomMember sweep** (shipped); the "recordings survive / UserId nullable
> / also-delete opt-in" part of the original design is **not** implemented. The personal room still orphans via
> the existing `OwnerUserId` SetNull FK.

**Files:** `src/Diariz.Api/Controllers/AdminUsersController.cs` (`Delete`, and `Deny`); `src/Diariz.Api/Contracts/ApiDtos.cs` (delete-user options); `tests/Diariz.Api.IntegrationTests/*` (the sweep + orphan needs real Postgres FKs)

- Before `await _users.DeleteAsync(user)`, sweep `RoomMembers WHERE PrincipalType == User AND PrincipalId == id` (load + `RemoveRange`; **not** `ExecuteDelete` - the in-memory provider can't). The personal room already orphans via the `OwnerUserId` SetNull FK - assert it survives with `OwnerUserId == null`.
- Add an **"also delete their recordings"** opt-in: `DELETE /api/admin/users/{id}?deleteRecordings=true`. Default false = recordings survive (shared rooms keep history). When true, delete the user's `Recording` rows first (cascades placements/segments).
- Integration test: a user in a shared room, deleted -> their `RoomMember` row is gone, their personal room is orphaned (not deleted), and a recording they shared into a shared room still exists.

Commit: `feat(api): deleting a user orphans their personal room and sweeps room memberships`.

---

## Task 4: shared `IconColorPicker` component

**Files:** create `apps/web/src/components/IconColorPicker.tsx` (+ test); refactor `ManageMeetingTypesModal.tsx:287-313` to use it. Reuses `MeetingTypeIcon` + `MEETING_TYPE_ICONS`.

- Props: `{ icon: string | null; color: string; onChange: (v: { icon?: string; color?: string }) => void }`. Renders the `<input type="color">` + the icon grid (pressed ring on the selected icon), identical markup to today.
- Swap it into `ManageMeetingTypesModal` (behaviour unchanged - its tests stay green). This is the targeted improvement the spec calls for; rooms + groups both consume it.

Commit: `refactor(web): extract the shared IconColorPicker`.

---

## Task 5: Manage Rooms modal + switcher entry

**Files:** create `apps/web/src/components/ManageRoomsModal.tsx` (+ test); `apps/web/src/lib/api.ts` (`createRoom`/`updateRoom`/`deleteRoom`/`getRoom`/`setRoomMember`/`removeRoomMember`); `apps/web/src/lib/types.ts` (room detail/member types); `RoomSwitcher.tsx` (add the **Manage Rooms** item, gated on `useAuth().permissions.manageRooms`, opening the modal); locales.

- Left: rooms list (icons + names) + **New Room** (creates `Room 1`, `Room 2`, ... ready to edit). Right: name, description (auto-expanding textarea), `IconColorPicker`, and the membership list (users + groups, each row with the six `RoomPermission` checkboxes). Close via top-right `x` (overlay-modal pattern from `ManageUsersModal`).
- Delete needs a **typed confirmation** (type the room name) - the spec's chosen guard. If the room holds shared recordings, the confirmation names them (Phase 5 refines the listing; Phase 4 shows the count).
- Only shared rooms are editable; the Personal room is read-only (no edit/delete/members).
- Manage Rooms appears in the switcher dropdown only for `manageRooms` holders.

Commit: `feat(web): Manage Rooms modal - create, edit, delete rooms and membership`.

---

## Task 6: recording into a shared room (the two-placement transaction)

**Files:** `RecordingsController.cs` (`Upload` gains `[FromForm] Guid? roomId`); `RoomScope.cs` (`ShareIntoRoomAsync(recordingId, roomId, sharedByUserId, sectionId?)`); `apps/web/src/lib/api.ts` (`upload` gains `roomId`); `Recorder.tsx` (send the current room when it is a shared room); tests both sides.

- The **main** placement is always the recorder's personal room (unchanged). When `roomId` is present **and** is a Shared room the caller may `CreateRecording` in, also create a non-main `RoomRecording` (`IsMainRoom=false`, `SharedByUserId=UserId`, `SharedAt=now`, `SectionId=null` - "the shared-room link is always ungrouped for now", per the spec). Reject `roomId` the caller can't record into (403) rather than silently dropping.
- Per the spec decision: the **main placement's folder** when recording into a shared room is **Ungrouped in the recorder's personal room** (so pass `sectionId: null` for the main placement in that case; keep the placement-preference behaviour only when recording into the personal room).
- Web: `Recorder` reads `useRoom().currentRoom`; when it is not personal, send its id as `roomId` and don't send a personal-room `sectionId`.

Commit: `feat: recording into a shared room shares it there while the main stays personal`.

---

## Task 7: flip folder/voiceprint/chat/meeting-type queries to the room

**Files:** `SectionPageController.cs`, `SectionAttachmentsController.cs`, `SpeakerProfilesController.cs`, `ChatController.cs`, `MeetingTypesController.cs`, and the section-scope spots in `RecordingsController.cs` (`:110, :923`). Tests updated to seed `RoomId` and assert room-scoping.

- Replace `x.UserId == UserId` with room membership: resolve the caller's current room (personal for now, or the room in scope) and filter by `x.RoomId == roomId`. Keep `RoomScope.RequireAsync`/`IsMemberAsync` as the gate. For MeetingType, platform (`RoomId == null`) stays visible to everyone; personal/room types filter by the caller's room membership.
- This is mechanical but broad - do it entity-by-entity, each its own commit, running the full unit + integration suites between. The queries were populated in Phases 2c/2d, so the data is ready.

Commits (one per entity): `refactor(api): scope <entity> by room, not UserId`.

---

## Task 8: drop the dead `UserId` columns + add the Rooms FKs

**Files:** entities (`Section.cs`, `SpeakerProfile.cs`, `ChatSession.cs` - remove `UserId`; add `Room` navigation), `DiarizDbContext.cs` (add the `RoomId` FKs behind `IsNpgsql`, drop the `UserId` indexes/config), a new migration `DropRoomScopedUserId`; `TranscriptChunk` loses its denormalised `UserId` too. Update every fixture/test that set those `UserId`s.

- Only after Task 7 proves nothing reads those `UserId`s. Add `RoomId` FK -> `Rooms` (the data is already backfilled + set-on-create). Drop `Section.UserId`, `SpeakerProfile.UserId`, `ChatSession.UserId`, `TranscriptChunk.UserId`.
- The migration is destructive: guard with an integration test that the columns are gone and the FKs enforce.
- **Recording keeps `UserId`** (now "recorded by") - do not touch it.

Commit: `feat(domain): drop the room-scoped UserId columns; add the Rooms FKs`.

---

## Task 9: docs, capabilities, release entry

**Files:** `docs/Overall_Synopsis_of_Platform.md` (shared rooms real: room CRUD, membership, two-placement share, user-delete orphaning, the completed `UserId`->`RoomId` migration), `docs/Data_Schema.md` (the FK additions + `UserId` drops + the new migration rows; the changed-entities become FK-backed), `README.md` + `docs/features.md` + About `CAPABILITIES` (shared rooms are now a real feature), `apps/web/src/lib/releases.ts` (extend the `0.119.0` entry - shared rooms land).

Commit: `docs: shared rooms (Phase 4)`. Push to PR #264.

---

## Self-review

- **Spec coverage:** room CRUD + membership (T1/T2/T5), user-delete orphan + sweep + opt-in (T3), IconColorPicker (T4), two-placement transaction (T6), the deferred `UserId`->`RoomId` flip + drops (T7/T8). Cross-room *manual* sharing UI (Share to another room / Remove from room on the detail toolbar), the Rooms/Recorded-by lines, and the RAG semi-join are **Phase 5**.
- **Personal room immutability** enforced in `RoomScope` (T1) and re-checked in the controller (T2) and UI (T5).
- **Flags-as-int** for `RoomDetailDto`/`RoomMemberDto`/`RoomMemberInput`.
- **Greenness:** T7/T8 are the risk; go entity-by-entity, full suites between, drop columns only after the flip. Every task ends green.
- **One release:** extend `0.119.0`.
