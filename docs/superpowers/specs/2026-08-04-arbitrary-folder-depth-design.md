# Arbitrary Folder Depth

**Date:** 2026-08-04
**Status:** Design, awaiting implementation plan

## 1. Goal

Let folders nest to arbitrary depth - `Customers` > `Acme Corp` > `Project Falcon` - instead of the
current two levels, and give the UI the two things a deeper tree needs to stay usable: a breadcrumb
that shows where you are at any depth, and a way to move things **across** branches.

Four decisions are already taken and are treated as settled throughout:

| Decision | Value |
| --- | --- |
| What "the recordings in a folder" means | The **whole subtree**, not just direct children |
| Maximum depth | **8** levels |
| Breadcrumb | Full path, middle collapsed when deep, every crumb clickable, a menu listing the full ancestor chain |
| Moving items | **Cut/paste** on the toolbar, covering **both recordings and folders**, deferred until paste |

## 2. The schema already supports this

`Section` is already a self-referencing tree: `ParentId`, `Position`, `Children`, with
`OnDelete(Cascade)` on the self-FK (`DiarizDbContext.cs:220`). Postgres cascades a self-referencing FK
recursively, so deleting a deep branch already behaves correctly.

**There is no migration in this work.** That matters beyond convenience: no migration means no
`MaintenanceController.CurrentFormat` bump, so every existing backup stays restorable.

The two-level cap is *policy*, living in three checks in `SectionsController`:

| Line | Check |
| --- | --- |
| `:89` | Create - the parent may not itself have a parent |
| `:130` | Reorder - the target parent may not itself have a parent |
| `:141` | Reorder - a folder that has children may not become a child |

## 3. Why not the obvious approaches

**"Just delete the three checks."** The cap is silently doing a second job. It is what makes a
`ParentId` **cycle** impossible today: with at most two levels there is no way to put A under its own
descendant. Remove the checks and `Reorder` can orphan an entire branch from the tree - it would vanish
from the nav and be reachable only through search. Both `drillView.ts:43` and `SearchController.cs:163`
already carry defensive cycle guards, which says the original authors expected this moment. The cap is
replaced by a descendant check, not deleted.

**Materialised path or nested sets instead of `ParentId`.** A `path` column (`/customers/acme/`) makes
subtree queries a single `LIKE` and would remove the level-by-level walk. Rejected: it requires a
migration (see above), it has to be maintained transactionally on every move, and the walk it replaces
runs over one room's folder list - tens of rows, already fetched. Optimising it is speculative.

**Recursive CTE for the subtree query.** Correct and fast, but Npgsql-only. Half the controllers in this
codebase are unit-tested against the EF in-memory provider specifically so they don't need Docker, and
`SearchController.SubtreeAsync` is already written as a provider-agnostic level-by-level walk for exactly
that reason. Matching it keeps the new helper unit-testable.

**Keep drag-and-drop as the only way to move things.** Structurally impossible. The nav shows **one level
at a time**, so source and destination are never on screen together. Drag-and-drop can reorder siblings
and move an item one step, and that is all it can ever do. This is why cut/paste is a requirement rather
than a convenience.

## 4. Non-goals

- **Pasting into a shared room.** The clipboard is personal-room only for now. Cross-room paste is a
  *copy*, not a move (a recording's placement in another room is a separate `RoomRecording` row), which is
  a different feature with its own permission questions.
- **Undo after paste.** Paste is a real server move; correcting it means cutting and pasting back.
- **Persisting the clipboard across a reload.** It lives for the session, in memory.
- **Changing how a recording is filed.** The folder remains a property of the `RoomRecording` placement.
- **Reworking search.** `SearchController` is already depth-agnostic and needs only the shared breadcrumb
  component for rendering its folder hits.

## 5. Architecture

### 5.1 The subtree helper - one behaviour, five copies

"The recordings in this folder" is computed as *this section plus its direct children* in five
independent places:

| Site | What it feeds |
| --- | --- |
| `ChatController.cs:468` | Folder chat context |
| `SectionPageController.cs:48` | Folder page stats, actions, notes, attachments |
| `SectionFormulaResultsController.cs:51` | Formula run scope |
| `FormulaRunProcessor.cs:281` | The map-reduce itself |
| `SectionSummaryProcessor.cs:80` | Folder summary and minutes |

At depth 3 a `Customers` summary would silently omit every meeting filed under a project folder - and
fail *quietly*, producing correct-looking output over the wrong set. All five collapse onto one helper,
extracted from the walk `SearchController.SubtreeAsync` (`:113`) already implements.

Two things fall out of this:

**It is behaviour-preserving today.** At depth 2, "whole subtree" and "direct children" are the same set.
The helper can therefore land first, on its own, with zero visible change to existing data - and it
resolves an existing disagreement, because the nav's count badge (`drillView.recordingCountOf`) already
promises the whole subtree while the folder page aggregates one level. At depth 2 those coincide; deeper,
they visibly diverge.

**`SectionSummaryProcessor` scopes by `UserId`; the other four scope by `RoomId`.** In a personal room at
depth 2 that difference is invisible. A shared-room folder summary reaching across a deeper tree would
expose it. It moves onto `RoomId` as part of the same change.

### 5.2 Depth and cycle validation

The three cap checks become three different rules. The create case and the move case are **not** the same
rule, which is the easiest thing to get wrong here:

| Operation | Rule | Failure |
| --- | --- | --- |
| Create | `depth(parent) < 8` | "Maximum folder depth reached" |
| Reparent | `depth(newParent) + height(movedSubtree) <= 8` | "That folder is too deep to hold this one" |
| Reparent | `newParent` is not the moved folder or any descendant of it | "You can't move a folder inside itself" |

Dragging or pasting a folder moves its **whole branch**, so dropping a 3-deep branch onto a folder at
level 6 must fail even though the target level is itself legal. Depth counts top-level as 1, so 8 means
eight levels of folder. The realistic worst case is 4 or 5; the cap is a guardrail that bounds the
breadcrumb and the path strings in the pickers, and turns a hypothetical cycle bug into a 400 rather than
a hung request.

### 5.3 `buildRecordingTree` becomes recursive

`recordingTree.ts:28` builds exactly `tops -> children`. It becomes a recursive build from a
`parentId -> children` index, with a visited set so a cycle that somehow reached the database cannot hang
the panel. Everything downstream of it - `childrenOf`, `breadcrumbOf`, `recordingCountOf` - already walks
generically.

### 5.4 The nav is already depth-agnostic

`drillView.ts` opens by saying so: *"Everything here walks `parentId` generically ... lifting the cap
should not touch the nav."* The drill position lives in the URL as `?in=<id>` (`drillRoute.ts`), so
browser-back already pops a level at any depth, and the folder row's count badge already recurses.

Exactly two spots hard-code depth 2:

- `RecordingsPanel.tsx:327` - `childrenCanNest = drill.sectionId === null` becomes a depth test, and feeds
  the `canNest` prop that gates both "New sub-section" and what a dropped folder does on a row.
- `drillView.ts:69` - `sectionCreateTarget`'s `blocked` branch becomes another `child`, with `blocked`
  retained for the new meanings (unknown id, or at the depth cap).

### 5.5 The breadcrumb

The constraint that drives the design: this is the **narrow left nav**, not a page header. The current
two-line stack (parent small above, current bold below, both `truncate`) exists because there is no
horizontal room. At realistic folder-name lengths perhaps two crumbs fit, so **the collapsed state is the
normal state** and the full path is the lucky case.

```
[<]  Customers / … / Acme Corp / Project Falcon  [v]
```

- **Every crumb drills** to that level. It does not open that folder's page.
- **The back arrow** pops one level, as today.
- **The trailing chevron is the menu**, listing the complete ancestor chain root-first and indented, and
  it is present whether or not the path is collapsed - so the full hierarchy is always one click away. The
  collapsed `…` is a **plain indicator, not a second trigger**: two controls doing the same thing in a
  strip this narrow would cost more than it returns. If width proves tighter than expected in build, the
  fallback is to drop the chevron and promote the `…` to the trigger.
- **"Open section page" must survive.** `DrillBreadcrumb.tsx:12` is emphatic that drilling and opening a
  folder's page are deliberately distinct targets - collapse them and a folder's page becomes unreachable
  once you have drilled into it. At depth it competes for the same pixels, so it moves into the menu as
  its first item.
- **Crumbs are drop targets.** Dragging a recording onto an ancestor crumb moves it up - nearly free once
  the crumbs are rendered, and it covers the most common correction ("this is filed too deep") without
  engaging the clipboard.

**Three consumers, one component.** The folder page (`SectionDetail.tsx`) has **no** breadcrumb at all
today - survivable at depth 2, disorienting at depth 5, where you open a folder page with no idea where it
sits. And search already returns an ancestor-name array (`FolderHitDto.crumb`, cycle-guarded at 32) that
currently renders flat. The component takes a path and renders it collapsed; the nav additionally wires
the click and drop behaviour.

### 5.6 The clipboard

A session-scoped context holding `{ kind: "recordings" | "folders", ids: string[], sourceRoomId }`,
mirroring how `selection.tsx` already provides selection to the panel. Cut never calls the server.

**Cut** is a toolbar action, enabled when one or more recordings are selected (individually or through
select-mode), and available on a folder's kebab menu.

**Cut items grey out with a dashed outline; they are not removed.** Removal reads as "the move happened",
and nothing has happened yet - if the user then cancels or navigates away, they cannot tell what state
they left things in. Grey means pending, which is exactly true.

**A persistent bar under the toolbar carries the clipboard**, because the whole point is *cut here, drill
elsewhere, paste* - by the time it matters, a toolbar button is out of the user's attention:

```
3 recordings ready to move    Paste into Project Falcon    Cancel
```

It names the destination, doubles as the paste control so nobody hunts for it, and gives Cancel a home.

**Paste targets the current drill level**, root included (which means Ungrouped). It is disabled, with the
reason stated rather than silently no-op, when:

| Condition | Reason shown |
| --- | --- |
| Destination is the source folder | Nothing to do |
| Browsing a shared room | Personal rooms only for now |
| Folder paste would breach depth 8 | That folder is too deep to hold this one |
| Folder paste targets itself or a descendant | You can't move a folder inside itself |

The shared-room case needs a **visible disabled** state, not an absent one: the clipboard survives a room
switch, so "cut in personal, drill into a shared room" is about three clicks away, and a missing button
there reads as broken.

**Folders are in the same clipboard.** A folder can only be dragged among the siblings currently on
screen, so without this a deep tree can be *built* but never *reorganised* - moving `Acme` from under
`Customers` to under `Prospects` would be impossible by any means in the UI. This is also where 5.2's two
distinct failure messages earn their keep.

### 5.7 Bulk move endpoint

`api.moveRecording` is one `PUT /api/recordings/{id}/section` per recording (`api.ts:924`), so pasting 20
is 20 round trips, with partial-failure states and a half-moved list.

Add a bulk sibling following the shape the codebase already uses for exactly this - `deleteAudioBulk`
posts `{ ids }` to `POST /api/recordings/audio/delete` alongside the single-item `DELETE` route
(`RecordingsController.cs:1289`). So: `POST /api/recordings/section` taking `{ ids, sectionId, roomId }`.
One call, one error, one cache invalidation.

Folder paste reuses `PUT /api/sections/reorder`, which already sets parent and position for a list of ids
in one call.

### 5.8 The pickers

Two components render the folder list flat as `Parent > Child` via `sectionTree.orderedSections`. At depth
5 both become long dropdowns of long strings:

- `MoveToSectionModal.tsx:90` - reached from a recording's kebab. It is the **discoverable** path to
  moving a recording, so it is rebuilt as a drill-down picker rather than retired in favour of cut/paste.
- `RecordingsSection.tsx:98` - the placement chooser for uploads and new recordings. It cannot simply be
  dropped; it gets the same treatment.

`orderedSections` itself becomes recursive, returning depth alongside each entry so the picker can indent
rather than concatenate ever-longer path strings.

### 5.9 Folder page transcript list

`FolderRecordingList.tsx:46` groups by direct sub-folder only. With 5.1 in place, a parent's page would
claim a summary and action list spanning the whole branch while its transcript list showed two levels.
The grouping recurses, each group labelled with its path relative to the folder being viewed.

## 6. Phasing

Each phase is a shippable PR. The order is chosen so the risky semantic change lands while it is still
provably a no-op.

| Phase | Content | User-visible? |
| --- | --- | --- |
| 1 | The subtree helper (5.1), five call sites onto it, `SectionSummaryProcessor` onto `RoomId` | No - identical at depth 2 |
| 2 | Lift the cap to 8: depth + height + descendant validation (5.2), recursive `buildRecordingTree` (5.3), the two nav spots (5.4) | Yes - nesting works |
| 3 | The breadcrumb component and its three consumers (5.5) | Yes |
| 4 | The clipboard, cut/paste for recordings and folders, bulk endpoint (5.6, 5.7) | Yes |
| 5 | Pickers and the folder-page list (5.8, 5.9) | Yes |

Phase 2 is the one that changes what users can do; phases 3-5 are what make it comfortable. Shipping 2
alone is coherent (the nav already drills and counts correctly at any depth) but the breadcrumb only shows
one ancestor, so 2 and 3 are best released together.

## 7. Testing

TDD throughout, per the repository's standing rule. The pure functions carry most of the weight:

- **Unit, web:** recursive `buildRecordingTree` including a cycle; `breadcrumbOf` at depth 8; the collapse
  logic (which crumbs survive at a given width budget); `sectionCreateTarget` at the cap; `orderedSections`
  depth output; clipboard reducer transitions (cut, cut again, cancel, paste).
- **Unit, API:** the depth, height, and descendant rules as a pure function over a folder list, so the
  awkward cases (move a branch under a deep target; move under own descendant; move to root) are cheap to
  cover. Then the controller cases for 400s.
- **Integration:** the subtree helper against real Postgres at depth 3+, asserting a grandchild's
  recordings appear in the folder page, the summary set, and a formula run - the exact silent-omission bug
  5.1 exists to prevent. Cascade delete of a 3-deep branch, asserting the recordings ungroup rather than
  vanish.
- **Component:** the breadcrumb collapsing and its menu; paste disabled states with their reasons.

## 8. Release checklist impact

The cap is asserted or described in more places than the code that enforces it:

- `SectionsControllerTests` and `SectionsIntegrationTests` assert the 400s.
- Three `EndpointDescription` blocks in `SectionsController` (`:51`, `:74`, `:114`) - these feed the MCP
  tool descriptions and `integrations/n8n-nodes-diariz/.../openapi.snapshot.json`, which must be
  regenerated.
- `docs/Data_Schema.md:616` and `:620` (the `ParentId` row and the nesting note).
- `docs/features.md:324` ("one level of nesting"), the README Features table (`:44`), and the About-box
  `CAPABILITIES` row - all three in lockstep.
- `docs/Overall_Synopsis_of_Platform.md:624` (`FormulaRunProcessor` "one level deep").
- The `newSectionNestCapped` i18n string changes meaning: "Sections can only be nested one level deep"
  becomes "maximum folder depth reached", a rare edge rather than a routine state. Plus new strings for
  cut, paste, the clipboard bar, and the four disabled reasons.
- Help articles under `apps/web/src/content/help/en/` are updated only where the **behaviour a user relies
  on** changes - moving recordings, and folder roll-ups now spanning the whole subtree.

Deployment surface: **server redeploy only**. Nothing here touches the desktop shell.

## 9. Risks

**A quiet wrong-set bug.** 5.1 is the whole risk of this work. A rollup that misses a branch produces
plausible output, so it will not be noticed by looking at it - which is why phase 1 ships alone, while the
tree is still shallow enough for the old and new behaviour to be provably identical.

**Cycles.** Guarded three ways: the descendant check on write, a visited set in the recursive tree build,
and the existing guards in `breadcrumbOf` and `SearchController`. The depth cap means a cycle that somehow
survived all of those still terminates.

**Breadcrumb width.** The collapse rule is the piece most likely to need iteration against real folder
names. Keeping it a pure function with its own tests makes that cheap.
