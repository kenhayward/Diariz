# Folder Cut/Paste Implementation Plan (Phase 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user move recordings and folders across the folder tree by cutting them in one place and pasting them in another.

**Architecture:** A session-scoped React context holds the cut items; nothing touches the server until paste. Recordings paste through a new bulk endpoint, folders through the existing reorder endpoint. A persistent bar under the toolbar carries the clipboard and doubles as the paste control.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core, xUnit, React 19 + TypeScript, Vitest + @testing-library/react, i18next.

## Why this exists

The nav shows **one level at a time**, so source and destination are never on screen together. Drag-and-drop can reorder siblings and move an item one step, and that is all it can ever do. With folders now nesting to 8 levels, a deep tree can be *built* but never *reorganised* - moving `Acme` from under `Customers` to under `Prospects` is impossible by any means in the UI today. Cut/paste is the only primitive that can express it.

## Decisions taken (2026-08-05)

| Question | Decision |
| --- | --- |
| Where pasted items land | **Bottom of the target folder, preserving their relative order** |
| Where you can paste | **The current drill level only**, root included (root = Ungrouped) |
| Cutting folders | **One folder at a time**, from its kebab. Recordings keep their existing multi-select |
| Pasting into a shared room | **Disabled, with the reason shown** - not hidden |

The paste-position decision also settles an existing inconsistency: dropping a recording onto a breadcrumb crumb currently files it at position 0 (top) while dropping onto a folder row appends it (bottom). Both become "bottom, preserving order".

## Global Constraints

- **TDD is mandatory.** Write the failing test, RUN it, watch it fail, then write the minimal code.
- **Test output must be pristine** - a passing run has no errors or warnings.
- **No em dashes or en dashes in user-facing text.** Plain ASCII hyphen.
- **Any new or changed i18n string goes in all four catalogues** (`en`, `de`, `es`, `fr`), translated consistently with each catalogue's existing terminology, preserving accented characters. Three separate tasks in the previous phases shipped English-only strings and left the others stale - do not repeat it.
- **`main` is branch-protected.** This lands as one PR.
- **One release:** `0.180.1` -> **`0.181.0`** (functional enhancement, Minor +1, Build reset) across `version.json` and its four mirrors, plus a `RELEASES[0]` entry whose `version` equals `version.json`.
- **No EF Core migration.** Nothing here changes the schema.
- **Deployment surface: server redeploy only.**
- `apps/web/src/lib/clipboard.ts` already exists for link-sharing. The move clipboard must NOT reuse that name.

## File Structure

**Created:**

| File | Responsibility |
| --- | --- |
| `apps/web/src/lib/moveClipboard.tsx` | The session-scoped cut clipboard context. Mirrors `selection.tsx`. |
| `apps/web/src/lib/moveClipboard.test.tsx` | Its tests. |
| `apps/web/src/lib/pasteTarget.ts` | Pure: given a clipboard and a drill position, may you paste, and if not why. |
| `apps/web/src/lib/pasteTarget.test.ts` | Its tests - the bulk of the validation coverage. |
| `apps/web/src/components/nav/ClipboardBar.tsx` | The persistent bar: count, destination, Paste, Cancel. |
| `apps/web/src/components/nav/ClipboardBar.test.tsx` | Its tests. |

**Modified:**

| File | Change |
| --- | --- |
| `src/Diariz.Api/Controllers/RecordingsController.cs` | New bulk move endpoint |
| `tests/Diariz.Api.Tests/RecordingsControllerTests.cs` | Its tests |
| `apps/web/src/lib/api.ts` | `moveRecordingsBulk` |
| `apps/web/src/components/RecordingsPanel.tsx` | Cut action, clipboard bar, paste wiring, greyed rows, crumb-drop position |
| `apps/web/src/components/nav/SectionRow.tsx` | Cut item on the kebab, greyed when cut |
| `apps/web/src/locales/{en,de,es,fr}/workspace.json` | New strings |

---

### Task 1: The bulk move endpoint

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Test: `tests/Diariz.Api.Tests/RecordingsControllerTests.cs`

**Interfaces:**
- Produces: `POST /api/recordings/section` taking `MoveRecordingsRequest(IReadOnlyList<Guid> Ids, Guid? SectionId, Guid? RoomId)`, returning `NoContent`. Ids the caller does not own are skipped, not an error - matching how the existing bulk audio-delete behaves.

**Context:** `api.moveRecording` is one `PUT /api/recordings/{id}/section` per recording, so pasting 20 is 20 round trips with partial-failure states. The precedent for a bulk sibling is `POST /api/recordings/audio/delete` (`RecordingsController.cs:1289`), which posts `{ ids }` alongside the single-item route.

**Ordering matters:** pasted recordings must land at the **bottom** of the target folder, preserving their relative order among themselves. Look at how the existing single-recording move assigns `Position`, and at `RoomRecordings` ordering in `SectionPageController`, before deciding how to number them. State in your report what rule you implemented and why.

- [ ] **Step 1:** Write the failing tests: (a) several recordings move into a folder in one call; (b) they land after recordings already in that folder, in the order given; (c) a `null` sectionId ungroups them; (d) an id belonging to another user is skipped rather than 404ing the whole call; (e) an empty id list is a no-op `NoContent`.
- [ ] **Step 2:** Run them, confirm they fail (the endpoint does not exist).
- [ ] **Step 3:** Add the DTO and the endpoint, with an `EndpointSummary`/`EndpointDescription` in the register the neighbouring endpoints use. The description is user-facing - it feeds the public API docs, the MCP tool list and the n8n node.
- [ ] **Step 4:** Run the tests; then `dotnet test tests/Diariz.Api.Tests` and `dotnet build Diariz.slnx`.
- [ ] **Step 5:** Editing an `EndpointDescription` trips the committed OpenAPI snapshot test. Regenerate with `npm run generate` in `integrations/n8n-nodes-diariz` and include `openapi.snapshot.json` and `generated/index.ts`. **Check the regenerated diff is confined to your change** - a whole-file rewrite means a line-ending problem, which has bitten this repo before.
- [ ] **Step 6:** Commit.

---

### Task 2: The clipboard context

**Files:**
- Create: `apps/web/src/lib/moveClipboard.tsx`, `apps/web/src/lib/moveClipboard.test.tsx`

**Interfaces:**
- Produces: `MoveClipboardProvider` and `useMoveClipboard()` returning
  `{ cut: { kind: "recordings" | "folders"; ids: string[]; sourceSectionId: string | null; sourceRoomId: string | null } | null, cutRecordings(ids, sourceSectionId, sourceRoomId), cutFolder(id, sourceRoomId), clear() }`.
- Consumed by Tasks 3-6.

**Context:** Mirror `apps/web/src/lib/selection.tsx` exactly - same shape of context, same default no-op object so a component rendered outside the provider still works. Session-scoped and in-memory: it must NOT persist across a reload, and it holds one kind at a time (a new cut replaces the previous one).

- [ ] **Step 1:** Write the failing tests: cutting recordings stores them; cutting a folder replaces a recordings cut; `clear()` empties it; the default context outside a provider is inert.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Implement, following `selection.tsx`'s structure and doc-comment register.
- [ ] **Step 4:** Run the focused tests, then the full web suite.
- [ ] **Step 5:** Commit.

---

### Task 3: `pasteTarget` - the pure validation rule

**Files:**
- Create: `apps/web/src/lib/pasteTarget.ts`, `apps/web/src/lib/pasteTarget.test.ts`

**Interfaces:**
- Consumes: the clipboard shape from Task 2; `SectionDto`; `depthOf` and `MAX_FOLDER_DEPTH` from `drillView.ts`; `heightOf` if you need it (see below).
- Produces: `pasteTarget(args) -> { kind: "ok" } | { kind: "blocked"; reason: PasteBlockedReason }` where `PasteBlockedReason` is a string union covering: `"same-folder"`, `"shared-room"`, `"too-deep"`, `"into-itself"`, `"empty"`.

**Context:** This is where every paste rule lives, kept pure so the awkward cases are cheap to test without a React tree. The four blocked states, from the decisions table:

| Condition | Reason |
| --- | --- |
| Destination is the folder the items were cut from | `same-folder` |
| The user is browsing a shared room | `shared-room` |
| Pasting a folder would push its branch past depth 8 | `too-deep` |
| Pasting a folder into itself or its own descendant | `into-itself` |

The last two mirror rules the **server already enforces** in `SectionsController.Reorder` - `depth(target) + height(movedBranch) <= 8`, and the target may not be the moved folder nor anything beneath it. Read that method before writing this so the client agrees with the server rather than inventing a near-miss. The web has `depthOf`; you will need the height of a moved branch too - add it to `drillView.ts` alongside `depthOf` if it is not there, with its own tests.

- [ ] **Step 1:** Write the failing tests, one per blocked reason plus the ok cases: recordings into a different folder, recordings into root, a folder into a legal parent. Include the boundary that must be **allowed**: a height-1 folder pasted into a depth-7 target lands at exactly 8.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run focused, then the full web suite.
- [ ] **Step 5:** Commit.

---

### Task 4: Cut affordances

**Files:**
- Modify: `apps/web/src/components/RecordingsPanel.tsx` (toolbar), `apps/web/src/components/nav/SectionRow.tsx` (kebab)
- Modify: the four `workspace.json` catalogues
- Modify/extend the matching test files

**Interfaces:**
- Consumes: `useMoveClipboard()` from Task 2.

**Context:** Two entry points, matching the decisions:
- **Recordings:** a toolbar action beside the existing select-mode actions, enabled when one or more recordings are selected. Follow how `mergeSelected` and the delete-audio action are wired - same `ToolbarButton`, same disabled discipline.
- **Folders:** a **Cut** item on a folder row's kebab, one folder at a time. Folders have no multi-select and this task does not add one.

Cutting records the source folder (the current drill level for recordings; the folder's own parent for a folder) so `pasteTarget` can detect a same-folder paste.

- [ ] **Step 1:** Write the failing tests: the toolbar cut is disabled with nothing selected and enabled with a selection; clicking it puts those ids on the clipboard; a folder kebab's Cut puts that folder on the clipboard.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Add the strings to **all four** catalogues, then implement.
- [ ] **Step 4:** Run focused, then the full web suite and `npm run build`.
- [ ] **Step 5:** Commit.

---

### Task 5: The clipboard bar

**Files:**
- Create: `apps/web/src/components/nav/ClipboardBar.tsx`, `apps/web/src/components/nav/ClipboardBar.test.tsx`
- Modify: the four `workspace.json` catalogues

**Interfaces:**
- Consumes: `useMoveClipboard()`, `pasteTarget()`.
- Produces: `<ClipboardBar onPaste={...} />`. Renders nothing when the clipboard is empty.

**Context:** A persistent bar under the toolbar, because the whole point is *cut here, drill elsewhere, paste* - by the time it matters, a toolbar button is out of the user's attention. It reads roughly:

```
3 recordings ready to move     Paste into Project Falcon     Cancel
```

It **names the destination**, doubles as the paste control so nobody hunts for it, and gives Cancel a home. When `pasteTarget` returns blocked, the Paste control is **visibly disabled with the reason shown**, never hidden - the clipboard survives a room switch, so the shared-room state is about three clicks away and a missing button reads as broken.

Pluralisation matters here (1 recording / 3 recordings / 1 folder). Use i18next's `_one`/`_other` suffix pairs rather than string concatenation - the established precedent is right beside where your keys will go: `confirmDeleteAudioBulk_one` / `confirmDeleteAudioBulk_other` in `workspace.json`, with `{{count}}` interpolated. Every language's catalogue needs both arms.

- [ ] **Step 1:** Write the failing tests: renders nothing when empty; shows the count and the destination name; Paste fires the callback; Cancel clears the clipboard; each blocked reason renders a disabled control with its message.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Add strings to all four catalogues, then implement.
- [ ] **Step 4:** Run focused, then the full web suite and `npm run build`.
- [ ] **Step 5:** Commit.

---

### Task 6: Wire it up - greyed rows, paste, and the crumb-drop fix

**Files:**
- Modify: `apps/web/src/components/RecordingsPanel.tsx`, `apps/web/src/components/nav/SectionRow.tsx`, `apps/web/src/lib/api.ts`
- Modify the matching tests

**Interfaces:**
- Consumes: everything from Tasks 1-5. Adds `api.moveRecordingsBulk(ids, sectionId, roomId)`.

**Context:** Three pieces.

1. **Cut items grey out with a dashed outline; they are NOT removed.** Removal reads as "the move happened", and nothing has happened yet - if the user then cancels or navigates away they cannot tell what state they left things in. Grey means pending, which is exactly true. This applies to recording rows and to a cut folder's row.

2. **Paste.** Recordings go through `api.moveRecordingsBulk` in one call; a folder goes through the existing `api.reorderSections`, which already sets parent and position for a list of ids. Both land at the **bottom of the target, preserving relative order**. On success, clear the clipboard and invalidate the `recordings` and `sections` queries. On failure, surface the error the way the panel's existing operations do (`setOpError`) and **leave the clipboard intact** so the user can retry.

3. **The crumb-drop fix.** `RecordingsPanel` currently calls `drop(sectionId, [], recordingId, null)` for a breadcrumb drop, which passes an empty id list and lands the recording at position 0 - the top - while dropping onto a folder row appends it. Make the crumb drop append too, so one gesture does not have two behaviours. `childrenOf(tree, sectionId).items` gives the correct existing id list.

- [ ] **Step 1:** Write the failing tests: a cut recording's row is visually marked; pasting calls the bulk API with the right ids and destination and then clears the clipboard; a failed paste keeps the clipboard; a crumb drop appends rather than prepends.
- [ ] **Step 2:** Run, confirm failure.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full web suite and `npm run build`.
- [ ] **Step 5:** Commit.

---

### Task 7: Release chores

**Files:**
- Modify: `version.json` + four mirrors, `apps/web/src/lib/releases.ts`, `README.md`, `docs/features.md`, `apps/web/src/content/help/en/organizing-folders.md`, `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1:** Bump to `0.181.0` in all five places.
- [ ] **Step 2:** Add the `RELEASES[0]` entry. `pr` will be supplied by the controller. Check the date against the entry below it - `releases.test.ts` asserts non-increasing dates, and a stale date has broken this twice before.
- [ ] **Step 3:** Update the About-box `CAPABILITIES` row (this is a scope change), the README Features row, and the `docs/features.md` bullet - **all three in lockstep**.
- [ ] **Step 4:** Extend `organizing-folders.md` with how to move things between folders. **ASCII only**, front matter intact. Name controls by their **actual** i18n strings, not plausible paraphrases - a previous help edit invented a "Move to folder" action that does not exist.
- [ ] **Step 5:** Add the bulk endpoint to `docs/Overall_Synopsis_of_Platform.md` where the other recording endpoints are described.
- [ ] **Step 6:** Full verification: web suite + build, `dotnet build Diariz.slnx`, both .NET suites. Commit. **Do not push or open a PR** - the controller does that.
