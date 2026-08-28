# Pinned action items: making the Actions tab opt-in

**Date:** 2026-08-28
**Status:** approved design, not yet implemented
**Version this targets:** 0.261.0 (functional enhancement, minor bump from 0.260.0)

## The problem

Action items are extracted automatically from every transcript. The cross-meeting **Actions** tab
(`RecordingsPanel` -> `ActionsTab`, backed by `GET /api/actions`) then shows *every* one of them, across
the whole library. At the volume the platform now produces, that list is unusable as a working view: most
of what it contains is minor, and a large share of it is assigned to other people. There is no way for a
user to say "this is one I actually intend to track".

Filtering does not fix it. The tab already has a person filter and a hide-completed toggle, and the list is
still overwhelming, because the problem is not that the wrong subset is shown - it is that inclusion is
automatic. Any filter over an automatic list is still a list somebody has to triage.

## The change, in one sentence

**An action reaches the cross-meeting Actions views only if someone has pinned it.** Everything else stays
where it was extracted, on its own recording's page.

Extraction is unchanged. The recording page is unchanged apart from gaining the pin control. Nothing is
deleted, hidden, or made harder to find on the recording it came from. What changes is that the aggregated
views become a curated list rather than a firehose.

**Day one, every user's Actions tab is empty.** That is intended and was confirmed. The empty state has to
say so, and say how to fill it - see "Empty states" below.

## Decisions taken

Four questions were open. All four are settled; each is recorded here with its consequence, because three of
them are the kind of decision that looks arbitrary later.

### 1. A pin is a property of the action, not of a user's relationship to it

One `Pinned` boolean column on `RecordingActions`. Not a per-user join table.

**Consequence, stated plainly:** in a **Shared Room**, the recording's owner decides what everyone in that
room sees pinned. A co-viewer cannot curate their own list of someone else's actions. This is the accepted
trade-off. It is coherent with how the rest of action editing already works - `ActionsController.Complete`
and every write in `RecordingActionsController` are owner-only, and `FolderActionsTable` already renders a
co-viewer's rows as read-only text with a disabled checkbox. Pinning simply joins that set.

In a user's own library - which is the case the complaint came from - every action belongs to one of their
own recordings, so the distinction does not arise.

If per-user pinning is ever wanted, it is a clean later migration: an `ActionPins(UserId, ActionId)` table,
with the existing column read as "the owner's pin" and used to seed it.

### 2. The folder Actions tab filters to pinned as well

`SectionDetail`'s Actions tab (`GET /api/sections/{id}/actions`, which aggregates every action across a
folder and its sub-folders) applies the same rule.

This keeps the user-facing rule free of exceptions: **the recording page is the only place an unpinned
action appears anywhere.** One sentence, no caveats, which is what makes it explainable in the help article.
The cost is that the folder Actions tab also starts empty.

### 3. The published endpoints keep their current default; the filter is opt-in

`GET /api/actions` is in the published OpenAPI document and is consumed by the `n8n-nodes-diariz` community
node as "List action items across meetings". Silently changing what it returns would break live n8n
workflows that nobody would think to check, and an npm-published node cannot be corrected after the fact.

So:

- Both list endpoints gain an **optional `pinned` query parameter**. Absent means unchanged behaviour -
  every action, exactly as today.
- The **web** passes `pinned=true`. The opt-in view is a property of the web client's request, not of the
  endpoint's default.
- Every action DTO gains a `pinned` field, so an integration can filter for itself either way.
- The **MCP `list_action_items` tool is unchanged** and keeps seeing every action. It is queried by a model
  answering a question, not browsed by a person, so the volume problem does not apply to it, and hiding
  unpinned actions from it would make the assistant wrong about the user's own meetings.

### 4. Re-extraction discards pins, the same way it already discards completion

`RecordingActionsController.Extract` and `ActionsProcessor` both **replace the whole action list**, deleting
the old rows and creating new ones with new ids. Completion state is already lost that way, and it is
documented as such. Pins follow the same rule: no matching heuristic, no preservation logic.

Re-extracting is an explicit "redo the actions on this meeting", and re-deciding what matters is a
reasonable thing to be asked to do at that moment. A text-matching heuristic would silently miss reworded
actions and over-apply to duplicate ones, which is worse than a rule that is simply consistent.

## Data model

One additive column.

| Column | Type | Notes |
|---|---|---|
| `RecordingActions.Pinned` | `boolean NOT NULL DEFAULT false` | Whether the action appears in the cross-meeting Actions views. Reversible. |

Migration name: `AddActionPinned`.

**Restore safety:** the column is additive with a default, so an older backup restores and migrates up
cleanly - its actions arrive unpinned, which is the correct reading of a backup taken before the concept
existed. **`MaintenanceController.CurrentFormat` is not bumped.** Backups are `pg_dump`-based
(`DatabaseBackup`), so the column travels without any serialisation change.

**No `PinnedAt`.** Nothing displays a pin date, and the aggregated lists keep their existing order (recording
`CreatedAt` descending, then `Ordinal`). `CompletedAt` exists because the UI shows a completion date; there
is no equivalent need here. Adding an unused timestamp now would be a column to migrate later for no reason.

## API

### DTO changes

`RecordingActionDto` gains a **trailing defaulted** parameter, exactly as `Completed`/`CompletedAt` were
added and for the same reason:

```csharp
public record RecordingActionDto(
    Guid Id, string Text, string Actor, string Deadline, int Ordinal,
    bool Completed = false, DateTimeOffset? CompletedAt = null, bool Pinned = false);
```

Five of its seven construction sites are export/chat/formula projections that do not track completion
(`ChatController` x2, `RecordingsController` x2, `FormulaRunProcessor`) and stay untouched. The two that do -
`RecordingActionsController.List` and `RecordingsController`'s detail projection - pass the real value.

`ActionListItemDto` gains `Pinned` as a **required** trailing field. Both of its construction sites
(`ActionsController.List`, `SectionPageController.Actions`) are real actions lists and supply it.

### Endpoints

| Endpoint | Change |
|---|---|
| `GET /api/actions` | New optional `?pinned=true`. Omitted = every action (unchanged). Items now carry `pinned`. |
| `GET /api/sections/{id}/actions` | Same optional `?pinned=true`; items carry `pinned`. |
| `POST /api/actions/pin` | **New.** `{ ids: [guid], pinned: bool }`. |
| `GET /api/recordings/{id}/actions` | Shape unchanged; each item now carries `pinned`. |
| MCP `list_action_items` | Unchanged. |

`POST /api/actions/pin` mirrors `POST /api/actions/complete` deliberately, down to its edge cases: it is
bulk, it joins through `Recording.UserId` so **ids the caller does not own are silently skipped** rather
than failing a mixed selection, and an empty list returns `204` without doing anything. Request record
`PinActionsRequest(IReadOnlyList<Guid> Ids, bool Pinned)`.

Pinning is deliberately *not* added to `PUT /api/recordings/{id}/actions/{actionId}`. That endpoint already
documents that it changes text, owner and deadline but not completion; state that governs the cross-meeting
views belongs with completion, on the `api/actions` controller.

The endpoint is bulk-shaped because that costs nothing and matches its sibling - but the UI wires only the
per-row toggle. See "Out of scope".

### Merge

`RecordingsController.Merge` copies each merged-away recording's actions into **new rows** on the survivor.
It will carry `Pinned` across, so a pinned action does not silently vanish from the Actions tab when a user
merges two parts of a meeting.

Noted and deliberately not changed: that same copy already drops `Completed`/`CompletedAt`, so a completed
action comes back un-completed after a merge. That is pre-existing behaviour, unrelated to this change, and
out of scope here. It is worth raising separately.

## Web

### Types and client

- `RecordingAction` and `ActionListItem` in `lib/types.ts` each gain `pinned: boolean`.
- `api.pinActions(ids: string[], pinned: boolean)` -> `POST /api/actions/pin`.
- `api.listAllActions(roomId)` and the folder actions fetch both send `pinned: true`.

### The three surfaces

**`ActionsTable`** (recording page, `RecordingDetail`) - the panel where actions are read and edited. Gains a
leading **Pin** column beside **Done**: a bi-state button per row, filled when pinned, outline when not.
Column widths shift to make room. This is the primary place a user decides what to track, so it sits at the
start of the row rather than hidden at the end.

**`ActionsTab`** (the left panel's Actions tab) - now shows pinned actions only. Each row gains a pin toggle;
using it unpins, and the row leaves the list on refetch. This component currently has no notion of row
ownership, so it takes a new `myUserId` prop and **disables the toggle on rows whose `recordedByUserId` is
somebody else** - the room case from decision 1. Without that it would offer a control that the API silently
ignores.

**`FolderActionsTable`** (folder page) - same per-row toggle, disabled for non-owned rows. This component
already receives `myUserId` and already gates edit/complete/delete on it, so the pin follows the existing
pattern with no new plumbing.

### Cache invalidation

A pin or unpin invalidates `["actions", "all"]`, `["recording"]` (so an open transcript reflects it), and the
folder actions key. Same set as `ActionsToolbar.markComplete` uses today, plus the folder key.

### Empty states

Both aggregated views will be empty for every user on the day this ships, so their copy is load-bearing
rather than decorative. Both strings are rewritten to explain the mechanism:

- `noActionsAll` today reads "No action items yet. Extract actions from a transcript to see them here." -
  which will be actively wrong. New copy names pinning as the way in.
- `folderNoActions` likewise.

New keys for the control's accessible labels (`pinActionAria` / `unpinActionAria`), interpolating the row or
the action text, following the existing `markCompleteAria` convention.

All new and changed strings land in **four locales** (`en`, `es`, `fr`, `de`) and use **plain hyphens only** -
`noFancyDashes.test.ts` covers the catalogues.

## Testing

TDD throughout: each guard below is written failing first, and **mutation-verified** - the exact regression
it exists to catch is introduced deliberately, the assertion is watched to fail, and the mutation reverted.
A guard that has not been seen to fail is not a guard.

### .NET unit (`tests/Diariz.Api.Tests`)

In `ActionsControllerTests`:
- `List` with `pinned=true` returns only pinned actions.
- **`List` with no `pinned` parameter still returns everything.** This is the guard on decision 3 - the one
  that fails if someone later "simplifies" the endpoint into pinned-by-default and breaks the n8n node.
- `Pin` sets and clears the flag; ids belonging to another user are skipped; an empty list returns `204`.

In `RecordingActionsControllerTests`:
- The per-recording list carries `pinned` through.
- `Extract` leaves the replacement actions unpinned, even when the ones it replaced were pinned - the guard
  on decision 4, asserting the accepted behaviour so a later change to it is a deliberate one.

### .NET integration (`tests/Diariz.Api.IntegrationTests`, `ActionsIntegrationTests`)

- The `pinned` filter on both list endpoints against real Postgres.
- `SectionPageController.Actions` with `pinned=true` across a folder and its sub-folders.
- Merge carries pins onto the survivor.

### Web (vitest)

- `ActionsTable`: the pin control reflects state and calls its handler; the recording page still shows
  **unpinned** actions (the guard that this change did not accidentally hide them at source).
- `ActionsTab`: the pin control is present, and is **disabled on a row whose `recordedByUserId` is not mine**.
- `RecordingsPanel`: asserts `api.listAllActions` is called **with the pinned flag**. This is the guard that
  the tab is opt-in at all; without it every other test here could pass while the tab still showed
  everything.
- `FolderActionsTable`: pin disabled for another user's row.

Two known traps in this repo apply directly:

- **`fireEvent.click` fires handlers on disabled controls**, so the two "disabled for someone else's row"
  assertions must use `userEvent` (installed) or they pass for a reason the browser never reproduces.
- If a test guards a method by **omitting it from the `vi.mock` factory**, adding `pinActions` to that
  factory destroys the guard. Any such case is converted to an explicit call assertion instead.

### Live verification

Browser-verified against the running stack before the PR: pin from the recording page and watch the action
appear in the Actions tab; unpin from the tab and watch it leave; confirm the folder tab agrees; confirm the
empty states read correctly on an account with nothing pinned.

## Release checklist

Functional enhancement, so **0.260.0 -> 0.261.0** across `version.json` and its seven mirrors (three
`package.json`, `Diariz.Api.csproj`, three `package-lock.json` with two fields each).

| Item | Needed | Why |
|---|---|---|
| `RELEASES[0]` in `lib/releaseNotes/current.ts` | Yes | Every PR. Note the file moved in 0.260.0 - it is no longer `lib/releases.ts`. |
| `CAPABILITIES` row in `lib/appInfo.ts` | Yes | The **Action items** row describes cross-meeting tracking; that behaviour changes. |
| README Features row | Yes | Same row, kept in lockstep. |
| `docs/features.md` bullet | Yes | Always alongside the README row. |
| `docs/Data_Schema.md` | Yes | New column and a migration-history row. |
| `docs/Overall_Synopsis_of_Platform.md` | Yes | Its "Action management (cross-meeting)" section enumerates `GET /api/actions` and `POST /api/actions/complete` by name; both the new endpoint and the changed view belong there. |
| Help article `content/help/en/action-items.md` | Yes | Behaviour a user relies on changes. Its "Across all your meetings" section currently promises every action appears there. |
| n8n `npm run generate` | Yes | A new endpoint and a new query parameter. The OpenAPI snapshot self-heals (run twice, commit the regenerated file); `generated/index.ts` does not. |
| GitHub issue first | No | Enhancement, not a fix. |
| `MaintenanceController.CurrentFormat` bump | No | Additive column, forward-restore safe. |

**Deployment surface: server redeploy only.** Nothing in `apps/desktop` is touched, so no desktop release
and no `v*` tag.

## Out of scope, deliberately

- **Bulk pin/unpin from the Actions toolbar.** The endpoint is bulk-shaped and the toolbar already has a
  selection model, so it is a small later addition. Not wired now - the request was a per-row control.
- **`PinnedAt` and pin-order sorting.** No UI needs either.
- **Per-user pins in shared rooms.** Decision 1; migration path noted there.
- **Auto-unpinning on completion.** Hide-completed already exists and does this job without discarding the
  user's choice.
- **Backfilling existing actions as pinned.** Day one is empty by design.
- **Fixing merge dropping `Completed`.** Pre-existing, unrelated, worth its own issue.
