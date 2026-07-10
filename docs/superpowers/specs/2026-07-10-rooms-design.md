# Rooms - shared user spaces

**Status:** Design, approved for planning
**Date:** 2026-07-10
**Source:** `untracked/Rooms Conceptual Spec.md` (conceptual), refined through brainstorming

## Summary

Today Diariz is strictly single-user: every entity carries a `UserId` and every controller appends
`&& x.UserId == UserId` to its queries. There is no sharing of any kind.

**Rooms** introduce a shared workspace. A room owns folders, recordings, calendar, actions, tags,
voiceprints, meeting types, and chats, and behaves exactly like today's personal space - except it has
members. Each user has an immutable **Personal Room**; additional **Shared Rooms** are created by
users holding the `ManageRooms` platform permission and are shared with users and groups, each member
carrying a per-room permission grid.

A recording's **main room is always its recorder's Personal Room**. Recording into a shared room creates
the recording in your personal room and automatically shares it into the room you are in. A recording may
be shared into any number of further rooms, landing in a folder of the target room's choosing. No shared
room is ever a main room.

**User Groups** replace the current Identity role system (`Standard` / `Administrator` /
`PlatformAdministrator`) as the source of platform-level authority.

## Decisions taken

These were settled during brainstorming and are not open for re-litigation during implementation
without revisiting this document.

| Decision | Choice | Why |
|---|---|---|
| Personal Room representation | A real `Room` row per user | One code path; no `null` branch in every query and permission check. Costs a backfill migration. |
| Groups vs roles | Groups **replace** roles | Two sources of truth for "can this user manage the platform?" can disagree. |
| Room-scoped data | Voiceprints, meeting types, chat conversations | They are workspace assets. |
| Storage quota | Stays per-user | Charged to `Recording.UserId` (who recorded it), even in a shared room. |
| Permissions for a shared recording | The room you are **viewing it in** | Matches "a room behaves as a shared user space". |
| Main room | Always the recorder's Personal Room | Avoids the deadlock below. A shared room can never hold a recording hostage. |
| Deleting a recording | Only from its main room | Sharing grants edit and unshare, never destruction. |
| Deleting a room | Allowed, with typed confirmation | Since no shared room is a main room, deletion unshares; it can never destroy audio. |
| Deleting a user | Orphans their Personal Room | Their recordings survive, so shared rooms keep their history. |
| Where a new recording lands | A per-user preference, defaulting to "the folder selected when Record was pressed" | It governs the main placement in the recorder's Personal Room. The shared-room link is always ungrouped for now. |
| Personal Room mutability | Immutable and private | Cannot be renamed, deleted, shared, or gain members. Enforced server-side. |
| Room transport | Explicit `roomId` in the URL | Visible, linkable, cacheable. Not an ambient header. |
| Room enforcement | Explicit filter via a `RoomScope` service | Same convention as today's `UserId` filter, one level up. Not EF global query filters. |
| Recording-to-room storage | A `RoomRecording` join row, main room is a flag | Sharing and ownership use one table; per-room folder falls out naturally. |

## Why the main room is always the Personal Room

An earlier draft let a shared room be a recording's main room. That produces a deadlock: a room cannot be
deleted while it holds recordings, a recording cannot be moved between rooms, and a recording whose main
room is the shared room cannot be unshared from it. The only escape is permanently deleting the audio.

Making the main placement always the recorder's Personal Room removes the deadlock by construction. A
shared room only ever holds *shared placements*, so deleting it unshares and never destroys.

Two consequences follow, and both are deliberate:

1. **Authority over a recording's existence follows the person, not the room.** Only the recorder can
   destroy a recording, and only from their Personal Room. A room's `RemoveOthersRecordings` permission
   therefore means *unshare from this room* and nothing more - which is why it is named that way rather
   than "delete".
2. **A user's Personal Room is retained when the user is deleted** (`OwnerUserId` → null, no members,
   hidden from every switcher). Without this, deleting a departing employee would silently destroy every
   meeting they ever recorded into a shared room. Purging an orphaned room requires `ManageUsers`, and the
   delete-user dialog offers "also delete their recordings" as an explicit opt-in.

Deleting a recording that is shared elsewhere prompts with the room names, because the recorder is
destroying other people's record of a meeting.

## Data model

### New entities

**`Room`**

| Column | Type | Notes |
|---|---|---|
| `Id` | `uuid` PK | |
| `Name` | `text` | Unique. |
| `Description` | `text?` | |
| `Icon` | `text?` | Key from the shared icon set. Null for personal rooms. |
| `Color` | `text?` | Hex. Null for personal rooms. |
| `Kind` | `int` | `Personal = 0`, `Shared = 1`. Append-only, as with every other int enum in Postgres. |
| `OwnerUserId` | `uuid?` | Set only for personal rooms. FK to `AspNetUsers` with `ON DELETE SET NULL` - a deleted user leaves an **orphaned** personal room whose recordings survive in the shared rooms they were shared into. |
| `CreatedAt` | `timestamptz` | |

Personal rooms render the owner's avatar (Google picture or initials) rather than a stored icon, so
`Icon`/`Color` stay null. A unique index on `OwnerUserId` (filtered `WHERE OwnerUserId IS NOT NULL`)
guarantees one personal room per user, and permits any number of orphaned rooms.

An **orphaned** room is `Kind = Personal` with `OwnerUserId IS NULL`. It has no members, appears in no
switcher, and is readable only through the shared placements of its recordings. Purging one requires
`ManageUsers`.

**`UserGroup`** - `Id`, `Name` (unique), `Description?`, `Icon?`, `Color?`, `Permissions`
(`PlatformPermission` flags), `IsSystem` (bool, true for the seeded Platform Administrators group).

**`UserGroupMember`** - `(GroupId, UserId)` composite PK. Cascade on both sides.

**`RoomMember`** - `(RoomId, PrincipalType, PrincipalId)` composite PK, plus `Permissions`
(`RoomPermission` flags). `PrincipalType` is `User = 0` / `Group = 1`; `PrincipalId` is the user or
group id. Modelled as one table with a discriminator rather than two, because effective-permission
resolution unions across both and a single table keeps that a single query.

**`RoomRecording`** - the placement of a recording in a room.

| Column | Type | Notes |
|---|---|---|
| `RoomId` | `uuid` | PK part 1. FK, cascade. |
| `RecordingId` | `uuid` | PK part 2. FK, cascade. |
| `IsMainRoom` | `bool` | Exactly one true row per recording, and it is always the recorder's Personal Room. |
| `SectionId` | `uuid?` | The folder **within that room**. Null = ungrouped. `ON DELETE SET NULL`, matching today's section behaviour. |
| `SharedByUserId` | `uuid?` | Null on the main-room row. |
| `SharedAt` | `timestamptz?` | Null on the main-room row. |

Constraints:

- Filtered unique index on `RecordingId WHERE IsMainRoom` - two main rooms are unrepresentable.
- `CHECK (IsMainRoom = false OR (SharedByUserId IS NULL AND SharedAt IS NULL))`.
- `SectionId` must belong to `RoomId`. Not expressible as a simple FK; enforced in the controller and
  covered by an integration test.
- **Invariant:** the `IsMainRoom` row's `RoomId` is the personal room of `Recording.UserId`. `IsMainRoom`
  is therefore derivable, but is stored anyway: it keeps `RoomScope`'s join simple and preserves the
  option of a future "move recording between rooms". Asserted by a test, not by the database.

### Changed entities

| Entity | Change |
|---|---|
| `Section` | `UserId` → `RoomId`. |
| `SpeakerProfile` | `UserId` → `RoomId`. |
| `ChatSession` | `UserId` → `RoomId`. |
| `MeetingType` | Gains `RoomId?`. Scope becomes platform (`RoomId == null`) or room. The current personal/platform split collapses into this. |
| `Recording` | Loses `SectionId` (moves to `RoomRecording`). **Keeps `UserId`, whose meaning changes to "recorded by"** - it drives the "Recorded by" line and storage-quota accounting. |
| `MeetingNote` | Keeps `UserId` as authorship. |
| `TranscriptChunk` | Loses the denormalised `UserId` (see below). |
| `UserSettings` | Gains `RecordingPlacementMode` (`int?`, null = the default `SelectedFolder`) and `RecordingPlacementSectionId` (`uuid?`, FK to `Sections` **`ON DELETE SET NULL`** - so a deleted folder degrades to ungrouped without any application code). |

Entities scoped transitively through `Recording` (`Transcription`, `Segment`, `Speaker`, `Summary`,
`MeetingMinutes`, `RecordingAction`, `RecordingTag`, `Attachment`, `RecordingCalendarLink`,
`ProfileContribution`) need no change: they inherit the room through their recording.

Entities that stay strictly personal: `UserSettings` (LLM endpoint and encrypted key),
`IcsCalendarSource`, `ApiAccessToken`, `McpAccessToken`, `PlatformSettings`.

### Permissions

```csharp
[Flags] public enum PlatformPermission { None = 0, ManageRooms = 1, ManageUsers = 2, ManagePlatform = 4 }

[Flags] public enum RoomPermission {
    None = 0, ManageRoom = 1, CreateRecording = 2, RemoveOthersRecordings = 4,
    ShareOut = 8, ManageContents = 16, EditOthersRecordings = 32,
}
```

```csharp
public enum RecordingPlacementMode { Ungrouped = 0, SelectedFolder = 1, SpecificFolder = 2 }
```

All three are stored as `int` and are **append-only**, like `RecordingStatus` and `RecordingSource`.

**Effective room permissions** for a user = the union of their own `RoomMember` row and the rows of
every group they belong to. Plus two overrides:

- The **personal room owner** implicitly holds every `RoomPermission`.
- The **recorder of a recording** (`Recording.UserId`) may always edit, delete, regenerate, and move
  *their own* recording, in any room it appears in, regardless of the grid. Straight from the
  conceptual spec.

**`ManageRooms` is not a read grant.** It permits creating, renaming, deleting rooms and editing their
membership. It does **not** permit reading a room's recordings unless the holder is a member. This is
called out because the opposite reading ("admins see everything") is a plausible default and is not
what is wanted.

**`RemoveOthersRecordings` is not a delete grant.** The conceptual spec called it "Delete / Remove Others
recordings", but because no shared room is ever a main room, it can only ever unshare. It is named for
what it does. Destroying a recording is possible only from its main (personal) room, by its recorder.

### The RAG filter (a known risk)

`TranscriptChunk` carries a denormalised `UserId` today specifically so pgvector KNN can filter by
owner without a join. With sharing, "chunks visible in room X" is a semi-join:

```sql
EXISTS (SELECT 1 FROM "RoomRecordings" rr
        WHERE rr."RecordingId" = c."RecordingId" AND rr."RoomId" = @room)
```

with an index on `RoomRecording(RoomId)`. This is correct, but a filtered vector scan does not
necessarily perform like the current one. **We measure before and after on a realistic corpus.** If it
regresses, the fallback is to denormalise `RoomId` onto the chunk for main-room search and accept the
semi-join only for shared recordings. Do not pre-optimise; do not assume parity either.

## Server surface

### `RoomScope`

A scoped service, resolved per request from the explicit `roomId`, that loads the caller's member rows
(direct and via groups) once and exposes:

- `Permissions` - the unioned grid, and `Require(RoomPermission.X)` throwing 403.
- `Recordings` - the base `IQueryable<Recording>` for the room, joined through `RoomRecording`. Every
  room-scoped controller query starts here, exactly as controllers start from `.Where(r => r.UserId ==
  UserId)` today. The scoping stays visible at each call site; nothing is filtered by magic.

**Membership is the read gate**, and a non-member gets **404, not 403** - they should not learn a room
exists.

Global EF query filters were considered and rejected: they cannot express the shared-in union without a
subquery on every read, they require `IgnoreQueryFilters()` on exactly the admin, worker-callback, and
sharing paths where mistakes hide, and they fail open. Explicit boilerplate that fails closed is
preferred.

### Routes

| Route | Notes |
|---|---|
| `GET /api/rooms` | Rooms the caller is a member of, with their effective permission grid. |
| `POST/PUT/DELETE /api/rooms[/{id}]` | Requires `ManageRooms`. Delete refuses any personal room (owned or orphaned; orphan purge needs `ManageUsers`). Deleting a shared room unshares its recordings - audio is never touched - and destroys only room-owned assets: folders, voiceprints, chats, room-scoped meeting types. The response body reports those counts so the UI can name what is lost in a typed confirmation. |
| `PUT /api/rooms/{id}/members` | Requires `ManageRooms` or `ManageRoom` in that room. Refuses personal rooms. |
| `GET/POST /api/groups[/{id}]` | Requires `ManageUsers`. |
| `/api/rooms/{roomId}/recordings`, `/sections`, `/tags`, `/actions`, `/chat`, `/speaker-profiles`, `/meeting-types` | Room-scoped collections. |
| `GET /api/recordings/{id}?roomId=` | Single entity; `roomId` supplies the permission context and is validated against `RoomRecording`. |
| `POST /api/recordings/{id}/share` | Body: target room + section. Requires `ShareOut` in the current room **and** `CreateRecording` in the target. |
| `DELETE /api/rooms/{roomId}/recordings/{id}` | Unshare. Requires `RemoveOthersRecordings` there, or being the recorder. **Refuses when that row is the main room** (that is a delete, and must be issued from home). |
| `DELETE /api/recordings/{id}` | Destroy. Only the recorder, only from the main room. Response lists the shared rooms it will vanish from so the UI can confirm. |

**Recording and upload** (`POST /api/rooms/{roomId}/recordings`, body carries an optional `sectionId`)
requires `CreateRecording` in `roomId`.

- When `roomId` is the caller's **personal room**, it writes one `RoomRecording` row: main placement,
  `SectionId = sectionId`.
- When `roomId` is a **shared room**, it writes **two** rows in one transaction: the main placement in the
  caller's personal room with `SectionId = sectionId`, and the shared placement in `roomId` **ungrouped**.

This keeps "no shared room is ever a main room" true by construction rather than by convention. `sectionId`
therefore always names a folder in the caller's **personal** room; a section belonging to any other room is
rejected.

### Where a new recording lands

The main placement is in the recorder's Personal Room, so "which folder?" is a question about *their own*
room, answered by a per-user preference (`UserSettings.RecordingPlacementMode`):

| Mode | Main placement lands in |
|---|---|
| `Ungrouped` | Ungrouped, always. |
| `SelectedFolder` (**default**) | The folder selected when Record was pressed - but only when recording into the Personal Room. In a shared room there is no Personal Room selection to honour, so it lands ungrouped. |
| `SpecificFolder` | `UserSettings.RecordingPlacementSectionId`, a Personal Room folder, regardless of which room the recording was made in. If that folder has been deleted the column is null and it lands ungrouped. |

**The link in a shared room is always ungrouped**, for now. Whether that wants its own preference is a
question for production feedback, not for this design.

The rules apply equally to **Record and Upload** - both create a recording. Drag-and-drop onto a folder
names its target explicitly and bypasses the preference entirely.

Three edge cases:

- **What counts as selected.** The section of the current view: the folder page you are on
  (`/rooms/:roomId/sections/:id`), or the folder containing the recording you have open, or the folder
  highlighted in the tree. Nothing selected means ungrouped. Sub-folders count, being sections like any
  other.
- **The folder is deleted mid-recording.** `sectionId` is validated on upload; a section that no longer
  exists, or that belongs to another room, is rejected and the recording lands **ungrouped rather than
  failing the upload**. Losing audio to a stale folder id would be indefensible.
- **The room is switched mid-recording.** The client captures the room and folder **at the moment Record is
  pressed**, not at upload - a long recording can outlive the selection. The recording lands where the user
  was standing when they clicked the button.

Note this changes behaviour in the Personal Room even for users who never create a shared room: today every
new recording lands ungrouped. The default (`SelectedFolder`) is the new behaviour, so the preference exists
partly to let anyone who preferred the old one set `Ungrouped` and keep it.

### Platform authorization

The `"Admin"` policy and the six `User.IsInRole(...)` checks are replaced by a permission-based
authorization handler that reads the caller's unioned group flags:
`[Authorize(Policy = "ManageUsers")]`, `"ManagePlatform"`, `"ManageRooms"`.

**Permissions are read from the database per request, not from a JWT claim.** A claim would go stale the
moment a user is added to or removed from a group, and would keep working until their token expired.
`TokenService` stops writing role claims; the web reads the caller's permissions from
`GET /api/user/profile` instead of decoding them out of the token.

A policy requires **any** of the flags it names, so a policy can express "manage users *or* manage
platform". That matters for `GET /api/platform-settings`, which Administrators read today (the Manage Users
modal shows the default quota): read is `ManageUsers | ManagePlatform`, write is `ManagePlatform` alone.

**Bootstrap.** A seeded, undeletable `Platform Administrators` group (`IsSystem = true`) holds all
three flags and contains the seed user. The API refuses to remove its **last** member. This preserves
today's invariant that the seed platform administrator cannot be deleted or demoted, now that the
`PlatformAdministrator` role is gone.

### Boundaries with no current room

- **Worker callback** (`internal/transcriptions/result`, shared-secret auth). Resolves the recording's
  **main room** and auto-identifies speakers against that room's voiceprints. Unambiguous: it is where
  the recording was made.
- **SignalR.** Clients join a group per room they are a member of, in addition to their user group.
  `RecordingStatusChanged` fans out to every room the recording sits in (main plus shared). A
  membership change triggers a client reconnect rather than server-side group mutation.
- **MCP and API tokens.** Per-user, no room switcher. Read tools span **every room the user is a member
  of**, name the room in their results, and accept an optional `room` argument to narrow. Defaulting to
  the personal room would make Claude blind to the user's shared meetings.

### Privacy: voiceprints

Moving `SpeakerProfile` to the room means an enrolment makes a biometric identity visible to every
member, and erasure is room-wide. Erasure therefore requires `ManageRoom`, or being the profile's
subject. This gate does not exist today and must be added with the move.

## UI

**Room switcher.** Replaces the "MEETINGS" `PanelHeader` in `Workspace.tsx:71` and its collapsed-rail
twin at `:85`. Shows the current room's icon and name; the dropdown lists rooms the user belongs to
(personal first, rendered with the user's avatar), then a horizontal rule, then **Manage Rooms** with a
house icon - shown only to holders of `ManageRooms`.

**The room lives in the URL.** A `/rooms/:roomId` layout route wraps the existing `index`,
`recordings/:id`, `sections/:id`, and `calendar-event/:eventId` children. Query keys gain the room id
(`["recordings", roomId]`, `["sections", roomId]`, `["tags", roomId]`, …), so a room switch isolates the
React Query cache naturally instead of requiring a scattershot invalidation across the dozen-plus keys
`RecordingsPanel` uses. Bare legacy links (`/recordings/:id`) redirect: resolve the recording's rooms,
prefer the personal room, else the first readable room.

**`RoomProvider`** exposes the current room and the caller's effective grid. It drives the UI: Record
and Upload disable without `CreateRecording`; kebab items hide or disable per permission. Disabled
controls carry a tooltip explaining *why*.

It also exposes the **selected folder**, which `Recorder` snapshots along with the room when Record is
pressed and resolves against the user's placement preference before uploading (see "Where a new recording
lands"). The Electron tray drives the same `Recorder` instance, so a tray-started recording inherits the
web app's current selection with no separate code path.

**Settings modal gains a `Recordings` tab.** `SettingsModal.tsx` is already tabbed with a single
OK/Cancel footer and a fixed height, so this is a new tab, not a new dialog. It holds the three placement
modes as radio buttons, with a folder chooser (Personal Room sections only, flattened "Parent › Child" as
`MoveToSectionModal` does) revealed by `Use a specific folder`. The tab is named for the category rather
than the setting, since more recording preferences are expected to land here.

**Manage Rooms modal.** Left: rooms with icons and names, plus **New Room** at the bottom, which
creates `Room 1`, `Room 2`, … ready to edit. Right: name, description (auto-expanding textarea), icon +
background colour, and the membership list - users and groups, each row carrying the six permission
checkboxes. Close via `×` top-right, matching the existing overlay-modal pattern.

**Shared `IconColorPicker`.** The icon + colour picker is currently inline in
`ManageMeetingTypesModal.tsx:287-313`. Rooms and groups both need it. Lift it into a shared component
with three real consumers - a targeted improvement to code we are already touching, not speculative
generality.

**Manage Users modal** gains a Groups tab (name, description, icon, colour, the three platform
permissions, members). The per-user "account type" column is replaced by group membership, since roles
are gone.

**Recording detail.** The Overview tab gains a **Rooms** line (icons and names, main room first) and a
**Recorded by** line. The toolbar and kebab - both fed from `recordingMenu.ts`, which remains the single
source of truth - gain **Share to another room** and **Remove from room**. Share opens a modal listing
rooms where the caller holds `CreateRecording`, then a folder picker for that room's sections (same
shape as `MoveToSectionModal`, pointed at another room's tree). Remove-from-room is enabled only outside
the main room, for the recorder or a holder of `RemoveOthersRecordings`. **Delete does not appear
outside the main room.**

## Migration

Two EF migrations, one per foundational phase.

**Migration 1 (Phase 1 - groups):**

1. Create `UserGroups`, `UserGroupMembers`.
2. Seed **two** groups, preserving today's privilege boundary exactly:
   - `Platform Administrators` (`IsSystem = true`): `ManageRooms | ManageUsers | ManagePlatform`. Members:
     the current holders of the `PlatformAdministrator` role.
   - `Administrators`: `ManageRooms | ManageUsers`. Members: the current holders of the `Administrator`
     role.

   Seeding a single group for both roles would grant every Administrator `ManagePlatform`, and with it
   backup/restore (`MaintenanceController`) and platform-settings writes - a privilege escalation that
   today's role check prevents.
3. Leave the Identity role tables in place but unused. Dropping them is a separate, later chore.

**Migration 2 (Phase 2 - rooms):**

1. Create `Rooms`, `RoomMembers`, `RoomRecordings`.
2. Backfill one `Personal` room per existing user (`OwnerUserId = user.Id`, name = the user's display
   name) and a `RoomMember` row granting the full grid.
3. Backfill `RoomRecordings`: one row per recording, `IsMainRoom = true`, `RoomId` = the owner's personal
   room, `SectionId` = the recording's current `SectionId`.
4. Add `RoomId` to `Sections`, `SpeakerProfiles`, `ChatSessions` (backfill from `UserId` → that user's
   personal room), and nullable `RoomId` to `MeetingTypes` (personal types → the owner's personal room;
   platform types stay null).
5. Add `UserSettings.RecordingPlacementMode` and `RecordingPlacementSectionId`, both null (so every
   existing user gets the `SelectedFolder` default).
6. Drop `Recording.SectionId`, `Section.UserId`, `SpeakerProfile.UserId`, `ChatSession.UserId`,
   `TranscriptChunk.UserId`, `MeetingType.UserId`.

Both backfills are data-heavy and must be written as SQL inside the migration, not as C# against the
`DbContext`. `Data_Schema.md` and `Overall_Synopsis_of_Platform.md` are updated in the same PR as the
migration that changes them, per the repository rule.

## Phasing

Each phase is a PR: independently reviewable, independently shippable, tests green at every step. The
app is never broken between phases.

| Phase | Content | Ships? |
|---|---|---|
| 1 | `UserGroup` + `UserGroupMember` + `PlatformPermission`, permission-based authorization handler, seeded Platform Administrators group, migration from roles. Manage Users modal gains the Groups tab. **No rooms yet.** | Yes - roles become groups, nothing else changes. |
| 2 | `Room`, `RoomMember`, `RoomRecording`, `RoomPermission`, `RoomScope`, the backfill migration, and the re-scoping of `Section` / `SpeakerProfile` / `ChatSession` / `MeetingType`. Server only; the personal room is the only room, so the API behaves identically. | Yes - invisible to users. |
| 3 | Room switcher, `/rooms/:roomId` routes, room-scoped query keys, `RoomProvider`, permission-driven UI gating, and the `Recordings` settings tab with the placement preference. Still only personal rooms exist. | Yes - a switcher with one entry, and new recordings start landing in the selected folder. |
| 4 | Manage Rooms modal, room creation/edit/delete (with the typed confirmation), membership editing, shared `IconColorPicker`, user-delete orphaning. Recording into a shared room writes the two-placement transaction. Shared rooms become real. | Yes - the feature lands. |
| 5 | Manual cross-room sharing: share/unshare endpoints, the Rooms + Recorded-by lines on Overview, Share and Remove-from-room in the toolbar and kebab, the delete-confirmation listing shared rooms, the RAG semi-join and its measurement. | Yes. |

Phase 2 is the risky one: it touches every controller. It is deliberately invisible to users, so a
regression surfaces as a failing test rather than a broken UI.

## Testing

TDD throughout, per the repository rule: failing test first, then the minimal code to pass.

**Unit (`Diariz.Api.Tests`, in-memory, no Docker).** Effective-permission resolution (user row, group
row, union, personal-room owner override, recorder override). `RoomScope.Require` throwing 403. The
"delete only from the main room" rule. The "`ManageRooms` is not a read grant" rule. The
"`RemoveOthersRecordings` cannot destroy" rule. The last-member guard on the Platform Administrators
group. Personal-room immutability.

**Integration (`Diariz.Api.IntegrationTests`, Testcontainers).** Everything the in-memory provider
cannot honour:

- The filtered unique index on `IsMainRoom`, and the invariant that the main row is the recorder's
  personal room - including after recording into a shared room, which must write exactly two placements:
  the main one in the preferred Personal Room folder, the shared one ungrouped.
- Uploading with a `sectionId` that was deleted mid-recording, or that belongs to another room, lands the
  recording ungrouped instead of failing.
- Deleting the folder named by `UserSettings.RecordingPlacementSectionId` nulls the column via
  `ON DELETE SET NULL`, and the next recording lands ungrouped.
- FK and cascade behaviour on room delete: placements and room assets go, recordings and audio stay.
- Deleting a user orphans their personal room (`OwnerUserId` → null) and leaves shared placements intact,
  so another member of the shared room still reads the recording.
- `SectionId` belonging to the wrong room is rejected.
- The `RoomRecording` semi-join returns main-room and shared-in recordings exactly once (no duplicate row
  when a recording is somehow placed twice).
- The pgvector chunk filter under the new semi-join, **with a recorded before/after timing**.
- The backfill migrations applied to a seeded pre-Rooms database.

**Web (`vitest`).** The switcher renders the personal room with an avatar and hides Manage Rooms without
the permission. Record and Upload disable without `CreateRecording`, with the explanatory tooltip. The
kebab omits Delete outside the main room and offers Remove from room instead. Deleting a shared recording
confirms with the room names.

Placement preference: each of the three modes resolves to the right `sectionId` at press time
(`SelectedFolder` in a shared room resolves to none); switching folders mid-recording does not change it;
the `Recordings` tab reveals the folder chooser only for `SpecificFolder`. The shared room's calendar renders recordings but no calendar events. Query
keys carry the room id, so switching rooms does not show the previous room's recordings.

**Worker (`pytest`).** Unaffected. The callback contract does not change shape; only the API's
interpretation of it does.

## Views inside a shared room

- **Tag cloud and Actions** include shared-in recordings. A shared placement makes a recording a full
  member of the room's content, not a second-class guest.
- **Calendar** in a shared room shows **recordings only, no calendar events**. `IcsCalendarSource` is a
  personal subscription, so a shared room has no calendar of its own, and surfacing the viewer's own
  invites inside someone else's room would leak their diary. The personal room keeps the full calendar,
  including invites and `RecordingCalendarLink` matching.

## Open questions

None. Both questions raised during brainstorming are resolved above.
