# Rooms Phase 3 - Switcher, Routing & Placement Preference

> **For agentic workers:** Execute task-by-task (executing-plans). Steps use checkbox (`- [ ]`) syntax. Keep the accumulation branch `feat/rooms-phase-2c-plus` (PR #264) green at every commit.

**Goal:** Surface rooms in the UI - a room switcher with one entry (the personal room), `/rooms/:roomId` routing, a `RoomProvider` exposing the current room + the caller's permission grid + the selected folder, permission-driven gating of Record/Upload, and a `Recordings` settings tab whose placement preference makes new recordings land in the selected folder.

**Architecture:** A new `GET /api/rooms` lists the user's rooms with their effective `RoomPermission` grid. On the web, `RoomProvider` (mounted inside `WorkspaceLayout`) reads that list, derives the current room from the `/rooms/:roomId` URL segment (defaulting to the personal room), and exposes it plus the selected folder. Query keys gain the room id so a switch isolates the React Query cache. Placement preference lives in two new `UserSettings` columns; `Recorder` snapshots the room + selected folder on Record and resolves the preference before uploading.

**Tech stack:** ASP.NET Core (.NET 10) + EF Core + Postgres; React 19 + TS + React Router 6 (`<Routes>`/`<Route>`, not `createBrowserRouter`) + React Query; vitest / xUnit / Testcontainers. TDD throughout.

**Standing constraints:** One-way data backfills live in EF migrations, never the seeder. New Postgres-only model config sits behind `Database.IsNpgsql()`. No em/en dashes in user-facing text. This whole phase is one release on PR #264 - extend the existing `0.118.4` release entry (do not add a new version) unless the user says otherwise.

---

## Context the executor needs (verified anchors)

- **Router:** `apps/web/src/App.tsx:63-74` - `/` → `<WorkspaceLayout/>` (RequireAuth) with children `index`→`<EmptyDetail/>`, `recordings/:id`, `sections/:id`, `calendar-event/:eventId`. `<BrowserRouter>` in `main.tsx:19`. The child `<Outlet/>` renders inside `Workspace.tsx:90`.
- **Left panel header:** `Workspace.tsx:71` `<PanelHeader title={t("panelMeetings")} .../>` and collapsed twin `:85` `<CollapsedRail label={t("panelMeetings")} .../>`. Both `PanelHeader` (120-145) and `CollapsedRail` (147-177) are file-local.
- **Auth/permissions:** `apps/web/src/auth.tsx` `useAuth()` exposes `permissions {manageRooms, manageUsers, managePlatform}`, `isAdmin`, `isPlatformAdmin`, sourced from `GET /api/user/profile` (`["user-profile"]`). `permissions.manageRooms` is plumbed end-to-end but has no UI consumer yet.
- **RoomScope** (`src/Diariz.Api/Services/RoomScope.cs`, `IRoomScope` 22-49): `PersonalRoomIdAsync`, `PermissionsAsync(userId, roomId)`, `IsMemberAsync`, `RequireAsync`, `RecordingsIn`, `PlaceInMainRoomAsync`, `SectionIdAsync`, `SetSectionAsync`. Personal-room owner implicitly holds `AllPermissions` (53-55).
- **Entities:** `Room {Id, Name, Description, Icon, Color, Kind (RoomKind Personal=0/Shared=1), OwnerUserId?, CreatedAt, Members}`; `RoomMember {RoomId, PrincipalType (User=0/Group=1), PrincipalId, Permissions (RoomPermission)}`. `RoomPermission [Flags]`: None=0, ManageRoom=1, CreateRecording=2, RemoveOthersRecordings=4, ShareOut=8, ManageContents=16, EditOthersRecordings=32.
- **Upload path:** `api.upload(blob, title, durationMs, source)` (`api.ts:250-264`) POSTs multipart to `/api/recordings`; **no SectionId**. Backend `RecordingsController.Upload` (198-255) calls `_rooms.PlaceInMainRoomAsync(rec.Id, UserId, sectionId: null)` at :250 - always ungrouped.
- **UserSettings** entity `src/Diariz.Domain/Entities/UserSettings.cs`; controller `UserSettingsController.cs` (`api/user/settings`) GET→`UserSettingsDto` (ApiDtos.cs 362-367), PUT→`UpdateUserSettingsRequest` (379-382).
- **SettingsModal** `apps/web/src/components/SettingsModal.tsx`: `type Tab = "ai"|"tools"|"quotas"|"maintenance"|"integration"` (:11), tablist 166-186, body ternary 191-457, footer 460-477, `onOk` PUTs settings at :96.

---

## Task 1: `GET /api/rooms` - list the caller's rooms with their permission grid

**Files:**
- Create: `src/Diariz.Api/Controllers/RoomsController.cs`
- Modify: `src/Diariz.Api/Services/RoomScope.cs` (add `RoomsForUserAsync`), `src/Diariz.Api/Contracts/ApiDtos.cs` (add `RoomListItemDto`)
- Test: `tests/Diariz.Api.Tests/RoomsControllerTests.cs`

**Design:** `RoomScope.RoomsForUserAsync(userId, ct)` returns the rooms the user is a member of (today: only their personal room), each with the caller's **effective** `RoomPermission` grid. The controller maps to `RoomListItemDto(Id, Name, Kind, Icon, Color, IsPersonal, Permissions)` where `Permissions` is the **int** bitmask (a `[Flags]` enum serialized via the global `JsonStringEnumConverter` would emit `"CreateRecording, ManageRoom"` and break the web's bit arithmetic - see the `flags-enum-serializes-as-string` memory: declare it `int`).

- [ ] **Step 1: Write the failing test** (`RoomsControllerTests.cs`)

```csharp
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

public class RoomsControllerTests
{
    private static RoomsController Build(DiarizDbContext db, Guid userId)
    {
        Users.Ensure(db, userId);
        return new(new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };
    }

    [Fact]
    public async Task List_ReturnsThePersonalRoom_WithFullPermissions()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<Diariz.Api.Contracts.RoomListItemDto>>(result.Value);
        var only = Assert.Single(rooms);
        Assert.True(only.IsPersonal);
        Assert.Equal((int)RoomPermission.CreateRecording, only.Permissions & (int)RoomPermission.CreateRecording);
        Assert.Equal((int)RoomPermission.ManageRoom, only.Permissions & (int)RoomPermission.ManageRoom);
    }

    [Fact]
    public async Task List_DoesNotReturnRoomsTheCallerIsNotAMemberOf()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        db.Rooms.Add(new Room { Id = Guid.NewGuid(), Name = "Someone else", Kind = RoomKind.Shared });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<Diariz.Api.Contracts.RoomListItemDto>>(result.Value);
        Assert.Single(rooms); // only the personal room, minted on demand
    }
}
```

- [ ] **Step 2: Run, watch it fail** - `dotnet test tests/Diariz.Api.Tests --filter FullyQualifiedName~RoomsControllerTests` (won't compile: no controller / DTO / service method).

- [ ] **Step 3: Add the DTO** to `ApiDtos.cs` (near the other feature DTOs):

```csharp
/// <summary>A room the caller belongs to, with the caller's effective permission grid as an int bitmask
/// (a [Flags] enum would serialize as "A, B" under the global string-enum converter and break the web's
/// bit arithmetic - see the flags-enum-serializes-as-string note).</summary>
public record RoomListItemDto(Guid Id, string Name, RoomKind Kind, string? Icon, string? Color, bool IsPersonal, int Permissions);
```
(Add `using Diariz.Domain.Entities;` if `RoomKind` isn't already in scope there.)

- [ ] **Step 4: Add `RoomsForUserAsync` to `IRoomScope` + `RoomScope`.** Return a small record list so the controller stays thin. Ensure the personal room is minted (call `PersonalRoomIdAsync`) so a brand-new user still gets one entry.

```csharp
// in IRoomScope
Task<IReadOnlyList<RoomListEntry>> RoomsForUserAsync(Guid userId, CancellationToken ct = default);

// new record near the top of RoomScope.cs (namespace scope)
public record RoomListEntry(Guid Id, string Name, RoomKind Kind, string? Icon, string? Color, bool IsPersonal, RoomPermission Permissions);

// impl
public async Task<IReadOnlyList<RoomListEntry>> RoomsForUserAsync(Guid userId, CancellationToken ct = default)
{
    var personalId = await PersonalRoomIdAsync(userId, ct); // mint on demand
    var memberRoomIds = await MemberRowsAsync(userId, ct);  // reuse the existing helper's room ids
    var ids = memberRoomIds.Select(m => m.RoomId).Append(personalId).Distinct().ToList();
    var rooms = await db.Rooms.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
    var result = new List<RoomListEntry>(rooms.Count);
    foreach (var r in rooms)
        result.Add(new RoomListEntry(r.Id, r.Name, r.Kind, r.Icon, r.Color,
            r.Kind == RoomKind.Personal && r.OwnerUserId == userId,
            await PermissionsAsync(userId, r.Id, ct)));
    // personal first, then shared by name
    return result.OrderByDescending(r => r.IsPersonal).ThenBy(r => r.Name).ToList();
}
```
Check the actual shape of `MemberRowsAsync` (RoomScope.cs ~182-195) and adapt - it may already return rows with `RoomId`. If it returns only the caller's own+group membership rows, that's exactly what's wanted.

- [ ] **Step 5: Add the controller.**

```csharp
using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>The rooms the signed-in user belongs to. Phase 3: read-only, and the only room is each user's
/// Personal room. Creation/membership editing arrive with Manage Rooms in Phase 4.</summary>
[ApiController]
[Authorize]
[Route("api/rooms")]
public class RoomsController(IRoomScope rooms) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var list = await rooms.RoomsForUserAsync(UserId, ct);
        return Ok(list.Select(r => new RoomListItemDto(
            r.Id, r.Name, r.Kind, r.Icon, r.Color, r.IsPersonal, (int)r.Permissions)));
    }
}
```

- [ ] **Step 6: Run the class green**, then `dotnet build Diariz.slnx` and the full unit suite. Commit: `feat(api): GET /api/rooms lists the caller's rooms with their permission grid`.

---

## Task 2: `RoomProvider` + `useRoom()` on the web

**Files:**
- Create: `apps/web/src/lib/rooms.tsx` (context/provider/hook), `apps/web/src/lib/rooms.test.tsx`
- Modify: `apps/web/src/lib/types.ts` (add `Room` list type + `RoomPermission` bitmask consts), `apps/web/src/lib/api.ts` (`listRooms`), `apps/web/src/components/WorkspaceLayout.tsx` (mount the provider)

**Design:** `useRooms()` fetches `["rooms"]`. `RoomProvider` derives the current room from the `roomId` URL param (once routing lands in Task 4; until then default to the personal room = the `IsPersonal` entry). Exposes `{ rooms, currentRoom, permissions, can(perm), selectedSectionId, setSelectedSectionId }`. `can(perm)` does `(currentRoom.permissions & perm) !== 0`.

- [ ] **Step 1:** Add to `types.ts`:

```typescript
export interface RoomListItem {
  id: string;
  name: string;
  kind: number;        // 0 = Personal, 1 = Shared
  icon: string | null;
  color: string | null;
  isPersonal: boolean;
  permissions: number; // RoomPermission bitmask
}

// RoomPermission flags - mirror src/Diariz.Domain/Entities/RoomPermission.cs (append-only).
export const RoomPermission = {
  ManageRoom: 1,
  CreateRecording: 2,
  RemoveOthersRecordings: 4,
  ShareOut: 8,
  ManageContents: 16,
  EditOthersRecordings: 32,
} as const;
```

- [ ] **Step 2:** Add `listRooms(): Promise<RoomListItem[]>` to `api.ts` (GET `/api/rooms`).

- [ ] **Step 3: Write the failing test** (`rooms.test.tsx`) - render a component under `RoomProvider` (with a QueryClient + a stubbed `api.listRooms` returning one personal room), assert `useRoom().currentRoom.isPersonal` and `can(RoomPermission.CreateRecording) === true`, and that `can` is false when permissions is 0.

- [ ] **Step 4:** Implement `rooms.tsx`. `currentRoom` = the room whose id matches `useParams().roomId`, else the personal room, else the first room. Persist `selectedSectionId` in state (default null). While the query is loading, `currentRoom` is undefined and `can()` returns false (fail closed).

- [ ] **Step 5:** Mount `<RoomProvider>` in `WorkspaceLayout.tsx` inside the existing provider stack (outside `<Workspace/>` so both the switcher and the recorder see it).

- [ ] **Step 6:** Green, commit: `feat(web): RoomProvider exposes the current room and the caller's permission grid`.

---

## Task 3: Room switcher replaces the "MEETINGS" panel header

**Files:**
- Create: `apps/web/src/components/RoomSwitcher.tsx`, `apps/web/src/components/RoomSwitcher.test.tsx`
- Modify: `apps/web/src/components/Workspace.tsx` (`:71`, `:85`), locale catalogs (`apps/web/src/locales/**` - the `workspace` namespace) if new strings are needed

**Design:** A button showing the current room's icon + name; clicking opens a dropdown listing rooms (personal first, drawn with the user's avatar), a divider, then **Manage Rooms** with a house icon **only if `permissions.manageRooms`** - but since the Manage Rooms modal is Phase 4, wire the item to a no-op/disabled state now (or omit until Phase 4; prefer omit to avoid dead UI). With one room the dropdown still works and reads naturally. Replaces `PanelHeader` at `:71`; the collapsed rail at `:85` shows the current room's icon.

- [ ] **Step 1: Write the failing test** - render `<RoomSwitcher>` under `RoomProvider` with two stubbed rooms (personal + a shared), assert both names appear on open and the personal one is first; assert Manage Rooms is absent without `manageRooms` and present with it (if included).
- [ ] **Step 2:** Fail (no component).
- [ ] **Step 3:** Implement `RoomSwitcher` reusing the existing dropdown/overlay pattern (mirror `UserMenu.tsx`). Personal room row uses the avatar (from `["user-profile"]`); shared rooms use their icon/color.
- [ ] **Step 4:** Swap it into `Workspace.tsx` at `:71` and adapt `:85`.
- [ ] **Step 5:** Green (`npm test`), commit: `feat(web): room switcher replaces the MEETINGS panel header`.

---

## Task 4: `/rooms/:roomId` routing, legacy redirects, room-scoped query keys

**Files:**
- Modify: `apps/web/src/App.tsx` (nest a `rooms/:roomId` layout route), the detail components that read `["recordings"]`/`["sections"]`/`["tags"]` (RecordingsPanel + the consumers listed in the map), and any `NavLink`/`navigate` that targets `/recordings/:id`, `/sections/:id`, `/calendar-event/:id` so they carry the room segment.

**Design:** Introduce `/rooms/:roomId` wrapping the four existing children; keep the bare children as **redirects** that resolve the caller's default room (personal) and forward. Query keys gain the room id: `["recordings", roomId]`, `["sections", roomId]`, `["tags", roomId]`. `RoomProvider` reads `roomId` from the params. This is the highest-risk task - do it in small commits and run `npm test` + `npm run build` after each sub-change. Keep a single helper (e.g. `roomPath(roomId, suffix)`) so link construction is consistent.

> **Scope guard:** with only the personal room, the *visible* behavior is unchanged - this task is infrastructure so Phase 4's multi-room switch isolates the cache. If any part threatens greenness, land the switcher+routing minimally (route param + provider wiring + redirects) and defer the per-key roomId suffix to its own commit, but do land it within this phase.

- [ ] **Step 1:** Add the nested route + redirects in `App.tsx`; update `RoomProvider` to read the param. Run `npm run build` + `npm test`.
- [ ] **Step 2:** Update the navigation links to include the room segment (the switcher sets it; folder/recording links preserve it). Test.
- [ ] **Step 3:** Append `roomId` to the `["recordings"]`/`["sections"]`/`["tags"]` keys and every matching invalidation site (the map lists them). Test + build.
- [ ] **Step 4:** Commit per sub-step: `feat(web): /rooms/:roomId routing and room-scoped query keys`.

---

## Task 5: Permission-driven gating of Record / Upload

**Files:**
- Modify: `apps/web/src/components/Recorder.tsx` (disable Record + Upload without `CreateRecording`, with a tooltip explaining why), `Recorder.test.tsx`

**Design:** `useRoom().can(RoomPermission.CreateRecording)` gates the Record and Upload buttons. In the personal room this is always true, so behavior is unchanged; the test proves the gate by stubbing a room with `permissions: 0`.

- [ ] **Step 1: Failing test** - render `Recorder` under a `RoomProvider` whose current room has `permissions: 0`; assert Record + Upload are `disabled` and carry a "why" tooltip.
- [ ] **Step 2:** Fail.
- [ ] **Step 3:** Add the gate (compose with the existing `disabled` conditions). Tooltip via `title`.
- [ ] **Step 4:** Green, commit: `feat(web): Record and Upload require CreateRecording in the current room`.

---

## Task 6: `UserSettings` placement-preference columns

**Files:**
- Modify: `src/Diariz.Domain/Entities/UserSettings.cs` (add `RecordingPlacementMode` enum column + `RecordingPlacementSectionId` Guid?), `src/Diariz.Domain/Entities/RecordingPlacementMode.cs` (new enum), `DiarizDbContext` config if needed
- Create: migration `AddRecordingPlacementPreference`
- Test: `tests/Diariz.Api.Tests` model test + a migration/round-trip integration test

**Design:** New enum `RecordingPlacementMode { Ungrouped = 0, SelectedFolder = 1, SpecificFolder = 2 }`. Default is `SelectedFolder` (spec: "the default is current selected folder"). Column is non-null with default `SelectedFolder`; `RecordingPlacementSectionId` is nullable (only meaningful in `SpecificFolder` mode). Both are plain columns - no data backfill needed (the default covers existing rows). Append-only enum ints.

- [ ] **Step 1: Failing model test** - seed a `UserSettings` with `RecordingPlacementMode.SpecificFolder` + a section id, assert round-trip.
- [ ] **Step 2:** Fail (no columns).
- [ ] **Step 3:** Add the enum + columns; `dotnet ef migrations add AddRecordingPlacementPreference --project src/Diariz.Domain --startup-project src/Diariz.Api` (works offline). Confirm the generated migration sets the default.
- [ ] **Step 4:** Green unit + a Testcontainers round-trip. Commit: `feat(domain): UserSettings carries a recording-placement preference`.

---

## Task 7: `Recordings` settings tab

**Files:**
- Modify: `SettingsModal.tsx` (add `"recordings"` to `Tab`, a tab button, a body panel, include the two fields in `onOk`'s PUT), `UserSettingsController.cs` + `ApiDtos.cs` (`UserSettingsDto` + `UpdateUserSettingsRequest` gain the two fields), `api.ts`/`types.ts` (web `UserSettings` gains them), `SettingsModal.test.tsx`

**Design:** A new **Recordings** tab (always visible, not admin-gated) with three radio options - *Always record into Ungrouped*, *Use the currently selected folder* (default), *Use a specific folder* - the last revealing a folder chooser (Personal Room sections only, flattened "Parent › Child" as `MoveToSectionModal` does). Persisted through the existing settings PUT.

- [ ] **Step 1: Failing test** - the tab renders the three radios, defaults to "selected folder", and reveals the chooser when "specific" is picked.
- [ ] **Step 2-4:** Extend the DTOs (server + web), add the tab + panel, wire `onOk`. Green (`npm test` + `dotnet test tests/Diariz.Api.Tests`). Commit: `feat(web): Recordings settings tab with the placement preference`.

---

## Task 8: New recordings land in the resolved folder

**Files:**
- Modify: `api.ts` (`upload` + `uploadFile` gain an optional `sectionId`), `RecordingsController.Upload` (accept `sectionId`, pass to `PlaceInMainRoomAsync`), `Recorder.tsx` (snapshot the room + selected folder on Record, resolve the placement preference, pass the section id), the upload context if it carries the id; tests on both sides.

**Design:** On Record press, `Recorder` snapshots `useRoom().selectedSectionId` (or null). Before upload it resolves the preference: `Ungrouped` → null; `SelectedFolder` → the snapshot; `SpecificFolder` → the configured section id. Per the user's decision, **recording into a shared room still files the main placement into Ungrouped in the recorder's personal room** (shared-room placement arrives in Phase 4) - so in Phase 3, resolve against the **personal room's** selected folder only. The backend `Upload` passes the section id into `PlaceInMainRoomAsync` (which already validates the section belongs to the room via `SetSectionAsync`-style checks; if not, guard: only honor a section id that belongs to the personal room, else fall back to null).

- [ ] **Step 1: Failing backend test** - `Upload` with a `sectionId` in the caller's personal room files the main placement into that folder; an alien section id is ignored (null).
- [ ] **Step 2: Failing web test** - `Recorder` in `SelectedFolder` mode with a selected folder passes that section id to `api.upload`; in `Ungrouped` mode passes null.
- [ ] **Step 3:** Implement both sides.
- [ ] **Step 4:** Green all suites (unit + integration + vitest). Commit: `feat: new recordings land in the selected folder per the placement preference`.

---

## Task 9: Docs, capabilities, release entry

**Files:** `docs/Overall_Synopsis_of_Platform.md` (room switcher + routing + placement preference), `docs/Data_Schema.md` (the `AddRecordingPlacementPreference` migration row + the two `UserSettings` columns), `docs/features.md` + `README.md` Features table + About-box `CAPABILITIES` (a room switcher is now user-visible), `apps/web/src/lib/releases.ts` (extend the `0.118.4` entry with the switcher + placement preference; **do not add a new version** - same PR, one release).

- [ ] **Step 1:** Update the four doc surfaces + capabilities in lockstep.
- [ ] **Step 2:** Extend the `0.118.4` release `summary` + `added`/`changed` for the now-visible switcher and the placement preference. Run `npm test -- releases`.
- [ ] **Step 3:** Commit: `docs: room switcher, routing and placement preference (Phase 3)`. Push to PR #264.

---

## Self-review checklist (run before starting)

- **Spec coverage:** switcher (T3), `/rooms/:roomId` + query keys (T4), `RoomProvider` + gating (T2/T5), Recordings tab + placement (T6/T7/T8) - all Phase-3 table items covered. Manage Rooms modal is explicitly **Phase 4**, so the switcher's Manage Rooms item is omitted/disabled here.
- **Flags-enum trap:** `RoomListItemDto.Permissions` is `int`, mirrored by a web `RoomPermission` const map - not the string-enum path.
- **Greenness:** T4 is the risk; sub-commit and build/test between each change. Every task ends green.
- **One release:** no version bump - extend `0.118.4`.
