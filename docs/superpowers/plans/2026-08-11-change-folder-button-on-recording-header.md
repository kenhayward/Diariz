# Change Folder Button on the Recording Header - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put a visible "Change folder" button immediately left of the folder breadcrumbs on the recording detail page, opening the existing folder picker, with the breadcrumbs repainting as soon as the move lands.

**Architecture:** No new components. The button is rendered by `RecordingDetail` inside the existing `folderPlacement` guard and reuses the `moving` state that already drives the kebab menu's "Move to section" item, so it opens the same `MoveToSectionModal`. Two latent defects in that modal's wiring are fixed on the way: it never invalidated the detail query (so the breadcrumbs went stale after a move), and this call site never told it which folder the recording was already in.

**Tech Stack:** React 19 + TypeScript + Vite, Tailwind v4, TanStack Query v5, react-i18next, Vitest + @testing-library/react (jsdom).

**Spec:** `docs/superpowers/specs/2026-08-11-change-folder-button-on-recording-header-design.md`

## Global Constraints

- **TDD is mandatory.** No production code without a failing test that preceded it. Watch every test fail for the *right reason* before implementing.
- **Mutation-check the bug-fix tests.** After they pass, revert the implementation line, confirm the test fails, restore it. A test that cannot fail is the dominant defect class in this repo.
- **No em or en dashes (`-` / `-`) in user-facing text.** Plain hyphen `-` only, in UI strings, i18n catalogs and release notes. Code and comments are unaffected.
- **All four locales stay in sync:** `en`, `de`, `es`, `fr` under `apps/web/src/locales/`.
- **Never `git add -A` in this repo.** Stage explicit paths only - a bare `-A` sweeps hundreds of agent scratch files into the commit.
- **Version scheme Major.Minor.Build.** This is a functional enhancement: `0.205.3` -> `0.206.0` (minor +1, build reset).
- **`main` is branch-protected.** Finish by pushing the branch and opening a PR. Never commit to `main`, never merge locally.
- **Working branch:** `feat/change-folder-button-on-recording-header` (already created; the spec is already committed on it).
- **Run web tests from `apps/web`:** `npm test`.
- **Deployment surface: server redeploy only.** No desktop shell files are touched.

---

### Task 1: Refresh the breadcrumbs when a move lands

The real bug. `MoveToSectionModal` invalidates only the `["recordings"]` list query, but the detail page's breadcrumbs are derived from the `["recording", id]` query. Those keys do not prefix-match in TanStack Query, so moving a recording from its own detail page leaves the chips showing the old folder until a reload.

**Files:**
- Modify: `apps/web/src/components/MoveToSectionModal.tsx` (the `move` and `createAndMove` functions, around lines 56-84)
- Test: `apps/web/src/components/MoveToSectionModal.test.tsx`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `MoveToSectionModal` invalidates `["recording", recordingId]` in addition to `["recordings"]`, on both the move path and the create-and-move path. Task 3 depends on this behaviour existing but does not re-test it.

- [ ] **Step 1: Write the failing tests**

Add to `apps/web/src/components/MoveToSectionModal.test.tsx`. The existing `renderModal` helper builds its own `QueryClient` internally and does not expose it, so add a second helper alongside it that returns the client - do not change `renderModal`, six existing tests use it.

Insert this helper directly below the existing `renderModal` function (after its closing `}`):

```tsx
/// Like `renderModal`, but hands back the QueryClient so a test can watch what the modal invalidates.
/// The breadcrumbs on the recording detail page are derived from the `["recording", id]` query, which is a
/// different key from the `["recordings"]` list - so "the list refreshed" does not mean "the breadcrumbs
/// refreshed", and only a direct assertion on the key distinguishes them.
function renderModalWithClient(currentSectionId?: string | null) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const invalidate = vi.spyOn(qc, "invalidateQueries");
  const view = render(
    <QueryClientProvider client={qc}>
      <MoveToSectionModal recordingId="rec-1" currentSectionId={currentSectionId} onClose={() => {}} />
    </QueryClientProvider>,
  );
  /// Every key this modal invalidated, flattened for readable assertions.
  const invalidatedKeys = () => invalidate.mock.calls.map((c) => JSON.stringify(c[0]?.queryKey));
  return { ...view, invalidatedKeys };
}
```

Then add this describe block at the end of the top-level `describe("MoveToSectionModal", ...)` block, immediately before its closing `});`:

```tsx
  /// The recording's detail page derives its folder breadcrumbs from the `["recording", id]` query. Moving
  /// the recording used to invalidate only the `["recordings"]` list, so the page you moved it *from* kept
  /// showing the old folder until a reload.
  describe("refreshes the recording it moved", () => {
    it("invalidates the recording's detail query after moving to a folder", async () => {
      const { invalidatedKeys } = renderModalWithClient(null);
      fireEvent.click(await screen.findByLabelText("Select Work"));

      await waitFor(() => expect(api.moveRecording).toHaveBeenCalled());
      await waitFor(() => expect(invalidatedKeys()).toContain(JSON.stringify(["recording", "rec-1"])));
    });

    it("still invalidates the recordings list after moving to a folder", async () => {
      const { invalidatedKeys } = renderModalWithClient(null);
      fireEvent.click(await screen.findByLabelText("Select Work"));

      await waitFor(() => expect(invalidatedKeys()).toContain(JSON.stringify(["recordings"])));
    });

    it("invalidates the recording's detail query after create-and-move", async () => {
      const { invalidatedKeys } = renderModalWithClient(null);
      await screen.findByLabelText("Filter folders");
      fireEvent.change(screen.getByLabelText(/new section name/i), { target: { value: "Ideas" } });
      fireEvent.click(screen.getByRole("button", { name: /create.*move/i }));

      await waitFor(() => expect(api.createSection).toHaveBeenCalled());
      await waitFor(() => expect(invalidatedKeys()).toContain(JSON.stringify(["recording", "rec-1"])));
    });
  });
```

- [ ] **Step 2: Run the tests and verify they fail for the right reason**

```bash
cd apps/web && npm test -- MoveToSectionModal
```

Expected: the two `["recording", "rec-1"]` tests FAIL. The failure must be the assertion on the invalidated keys (the array contains only `["recordings"]`, or `["recordings"]` and `["sections"]`), **not** a render error, not "spyOn is not a function", not a missing import. The third test ("still invalidates the recordings list") must PASS already - it pins existing behaviour so the next step cannot fix one query by dropping the other.

If `QueryClientProvider`, `QueryClient`, `waitFor`, `fireEvent` or `vi` are not already imported in this file, add them - check the existing import block at the top first; all of them are already there.

- [ ] **Step 3: Implement**

In `apps/web/src/components/MoveToSectionModal.tsx`, in `move()`, after the existing `qc.invalidateQueries({ queryKey: ["recordings"] });`:

```js
      qc.invalidateQueries({ queryKey: ["recordings"] });
      // The detail page's folder breadcrumbs come from the recording's own query, which is a different key
      // from the list - without this the page you moved it from keeps showing the old folder.
      qc.invalidateQueries({ queryKey: ["recording", recordingId] });
```

And the same addition in `createAndMove()`, after its existing `qc.invalidateQueries({ queryKey: ["sections"] });`:

```js
      qc.invalidateQueries({ queryKey: ["sections"] });
      qc.invalidateQueries({ queryKey: ["recording", recordingId] });
```

`recordingId` is already a prop on this component - no new prop is needed.

- [ ] **Step 4: Run the tests and verify they pass**

```bash
cd apps/web && npm test -- MoveToSectionModal
```

Expected: PASS, all tests in the file including the pre-existing ones. No new warnings in the output.

- [ ] **Step 5: Mutation-check both new assertions**

Delete the line `qc.invalidateQueries({ queryKey: ["recording", recordingId] });` from `move()` only. Re-run:

```bash
cd apps/web && npm test -- MoveToSectionModal
```

Expected: "invalidates the recording's detail query after moving to a folder" FAILS; "invalidates the recording's detail query after create-and-move" still PASSES. Restore the line, delete the one in `createAndMove()` instead, re-run, and confirm the opposite. Restore it.

This proves the two tests are independently wired to the two code paths rather than both riding on one. Do not skip it - a test that passes with the implementation removed is worse than no test.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/MoveToSectionModal.tsx apps/web/src/components/MoveToSectionModal.test.tsx
git commit -m "fix(web): refresh the moved recording's own query, not just the list"
```

---

### Task 2: Open the picker on the recording's current folder

`RecordingDetail` renders `MoveToSectionModal` without `currentSectionId`, so the modal falls back to its `UNKNOWN_SECTION` sentinel and marks nothing. The page has already computed the answer as `folderPlacement`.

Note the modal's own three-state contract: `undefined` means "the caller does not know", `null` means "filed at the room's top level" and is a real, markable value. Passing `?? null` is correct because a `folderPlacement` with no `sectionId` genuinely is at the top level.

**Files:**
- Modify: `apps/web/src/pages/RecordingDetail.tsx` (the `MoveToSectionModal` render, around line 1746)
- Test: `apps/web/src/pages/RecordingDetail.test.tsx`

**Interfaces:**
- Consumes: `folderPlacement` - already computed in `RecordingDetail`'s render as `rec.rooms?.find((r) => r.id === currentRoom?.id)`, shape `{ id, name, icon, color, isMain, sectionId } | undefined`. It is in scope at the modal's render site (both sit after the `if (!rec) return` guard).
- Produces: nothing later tasks consume.

- [ ] **Step 1: Write the failing test**

Add to `apps/web/src/pages/RecordingDetail.test.tsx`, inside the existing `describe("RecordingDetail folder chips", ...)` block (it already has the `renderInRoom` and `inRoom` helpers and the `roomState.currentRoom` setup this test needs), immediately before that block's closing `});`:

```tsx
  /// Opening the picker from a page that already knows where the recording is filed should show that folder
  /// as the current one. The modal has a distinct "caller does not know" state, and this call site used to
  /// land in it, so the picker marked nothing.
  describe("opens the folder picker on the recording's current folder", () => {
    /// Open the picker through the kebab. The new button does not exist until Task 3, so this keeps the
    /// prop fix independently testable - and it doubles as a guard that the menu item survives.
    async function openPicker() {
      await screen.findByRole("navigation", { name: /folder/i });
      fireEvent.click(screen.getByRole("button", { name: "More actions" }));
      fireEvent.click(await screen.findByRole("button", { name: /move to section/i }));
    }

    it("marks a top-level folder as current", async () => {
      renderInRoom(inRoom("cust"));
      await openPicker();

      // FolderPicker marks the current folder with aria-current on that row's select control.
      const current = await screen.findByLabelText("Select Customers");
      await waitFor(() => expect(current.getAttribute("aria-current")).toBe("true"));
    });

    /// A nested folder's own row is not on screen when the picker mounts at the top level. FolderPicker
    /// deliberately does NOT drill to reveal it - it shows a "Selected: {path}" line above the list
    /// instead. So the visible proof for a nested current folder is that line, not an aria-current row.
    it("names a nested folder in the selected-path line", async () => {
      renderInRoom(inRoom("acme"));
      await openPicker();

      expect(await screen.findByText(/Selected:.*Acme Corp/)).toBeTruthy();
    });

    it("marks the room's top level as current for an unfiled recording", async () => {
      renderInRoom(inRoom(null));
      await openPicker();

      const root = await screen.findByLabelText("Select Ungrouped");
      await waitFor(() => expect(root.getAttribute("aria-current")).toBe("true"));
    });
  });
```

Three cases because `FolderPicker` proves "this is the current folder" three different ways, and only the first looks like the obvious assertion:

- **Top-level folder** - its row is visible, so it carries `aria-current="true"`.
- **Nested folder** - its row is *not* visible. `FolderPicker` shows `folderPickerCurrentSelection` ("Selected: {{path}}") above the list rather than seeding its drill position from `selectedId`; see its doc comment. Asserting `aria-current` on a top-level row here would fail even with the fix correctly applied.
- **`null`** - the "Ungrouped" root row is always rendered and marked. This is the case that would silently regress if someone "simplified" `?? null` into passing `folderPlacement?.sectionId` directly, which would send `undefined` and mark nothing.

- [ ] **Step 2: Run the test and verify it fails**

```bash
cd apps/web && npm test -- RecordingDetail
```

Expected: all three FAIL, because the modal received `undefined` and used its unknown-section sentinel - the first and third on `aria-current` being `null`, the second because no "Selected: …" line renders at all. The dialog itself must open successfully in each; if the failure is instead a crash inside the modal, stop and read the error before continuing (see the mock note in Task 3, Step 1).

- [ ] **Step 3: Implement**

In `apps/web/src/pages/RecordingDetail.tsx`, add the `currentSectionId` prop to the existing `MoveToSectionModal` render:

```jsx
      {moving && (
        <MoveToSectionModal
          recordingId={id}
          // The page already knows where this is filed, so the picker opens on it. `?? null` is deliberate:
          // the modal reads `undefined` as "caller does not know" and `null` as "the room's top level".
          currentSectionId={folderPlacement?.sectionId ?? null}
          roomId={currentRoom && !currentRoom.isPersonal ? currentRoom.id : undefined}
          onClose={() => setMoving(false)}
        />
      )}
```

- [ ] **Step 4: Run the tests and verify they pass**

```bash
cd apps/web && npm test -- RecordingDetail
```

Expected: PASS, and every pre-existing test in the file still passes.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/pages/RecordingDetail.tsx apps/web/src/pages/RecordingDetail.test.tsx
git commit -m "fix(web): open the folder picker on the recording's current folder"
```

---

### Task 3: The Change folder button

**Files:**
- Modify: `apps/web/src/pages/RecordingDetail.tsx` (the `folderPlacement && <FolderChips …>` block, around line 1658)
- Modify: `apps/web/src/components/detail/FolderChips.tsx` (drop `-mt-1`, update its doc comment)
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Test: `apps/web/src/pages/RecordingDetail.test.tsx`

**Interfaces:**
- Consumes: `setMoving` (existing `useState` at `RecordingDetail.tsx:350`), `folderPlacement` (see Task 2), and the modal wiring from Tasks 1-2.
- Produces: nothing later tasks consume.

**Why the button is not inside `FolderChips`:** `FolderChips` renders a `<nav aria-label="Folder this recording is in">` - a navigation landmark whose contents are all destinations. "Change folder" is an action, so putting it inside would misdescribe it. There is also a concrete test consequence: several existing tests assert `within(chips).getAllByRole("button")` equals an exact array like `["Personal", "Customers", "Acme Corp"]`. A button inside the nav would break all of them. Keeping it in the wrapper keeps `FolderChips` purely navigational and those assertions honest.

- [ ] **Step 1: Add the i18n key to all four locales**

This is not production logic and the tests below read the rendered English string, so it comes first.

In `apps/web/src/locales/en/workspace.json`, next to the existing `"folderChipsLabel"` key (line ~462):

```json
  "changeFolder": "Change folder",
```

`apps/web/src/locales/de/workspace.json`:

```json
  "changeFolder": "Ordner ändern",
```

`apps/web/src/locales/es/workspace.json`:

```json
  "changeFolder": "Cambiar carpeta",
```

`apps/web/src/locales/fr/workspace.json`:

```json
  "changeFolder": "Changer de dossier",
```

Plain hyphens only - none of these strings should contain `-` or `-`. Keep valid JSON: the preceding line needs its trailing comma.

- [ ] **Step 2: Write the failing tests**

Add to `apps/web/src/pages/RecordingDetail.test.tsx`, inside the existing `describe("RecordingDetail folder chips", ...)` block, before its closing `});`.

Before writing these, add `moveRecording: vi.fn(),` to the `vi.mock("../lib/api", ...)` factory near the top of the file (the object listing `getRecording`, `listSections` and the rest). The factory currently omits it. That omission is not acting as a guard for anything - it is simply unused so far - but opening the picker from this page brings a component that can call it into the tree, and an absent method fails as an opaque crash rather than a clear assertion. Add it as a plain `vi.fn()`.

```tsx
  /// The chips say where the recording is filed; this button is how you change it. It sits with them, left
  /// of the breadcrumbs, rather than being buried in the kebab menu.
  it("shows a Change folder button beside the breadcrumbs", async () => {
    renderInRoom(inRoom("acme"));

    expect(await screen.findByRole("button", { name: /change folder/i })).toBeTruthy();
  });

  it("opens the folder picker when Change folder is clicked", async () => {
    renderInRoom(inRoom("acme"));

    fireEvent.click(await screen.findByRole("button", { name: /change folder/i }));

    expect(await screen.findByRole("dialog", { name: /move to section/i })).toBeTruthy();
  });

  /// It is an action on the chips, not one of them. The chip row is a navigation landmark whose every
  /// control is a destination, and several tests above assert exactly which buttons live inside it.
  it("keeps the button outside the breadcrumb navigation landmark", async () => {
    renderInRoom(inRoom("acme"));

    const chips = await screen.findByRole("navigation", { name: /folder/i });
    expect(within(chips).queryByRole("button", { name: /change folder/i })).toBeNull();
  });

  it("renders no Change folder button when the recording is not placed in the room being viewed", async () => {
    renderInRoom(inRoom("acme", "some-other-room"));

    await screen.findByText(/Mic 6\/26\/2026/);
    expect(screen.queryByRole("button", { name: /change folder/i })).toBeNull();
  });
```

- [ ] **Step 3: Run the tests and verify they fail**

```bash
cd apps/web && npm test -- RecordingDetail
```

Expected: the first two FAIL with `findByRole` unable to find a button named "Change folder". The third and fourth PASS trivially (nothing named that exists yet) - that is expected and fine; they are regression guards that become meaningful once Step 4 lands, and Step 5 re-runs them against the real button.

If the first test fails because the *English string* is missing rather than the button, revisit Step 1 - `src/test-setup.ts` pins i18n to `en`, so the rendered label is the `en` catalog value.

- [ ] **Step 4: Implement**

In `apps/web/src/pages/RecordingDetail.tsx`, replace the existing chips block:

```jsx
      {folderPlacement && (
        <FolderChips
          roomName={currentRoom?.name ?? ""}
          crumbs={folderCrumbs}
          onSelect={openFolderInList}
        />
      )}
```

with:

```jsx
      {folderPlacement && (
        // -mt-1 counteracts the hero's space-y-2.5 so the row sits tight under the name as part of the
        // title block. It lives here rather than on FolderChips' nav because the nav is no longer the
        // outermost element of the row.
        <div className="-mt-1 flex flex-wrap items-center gap-2">
          {/* An action on the path, not a step in it - so it stays outside FolderChips' navigation
              landmark. Square corners against the chips' pills are what tell the two apart; it carries no
              folder icon, because FolderChips already opens with one and two adjacent folder glyphs blur
              which belongs to which control. */}
          <button
            type="button"
            onClick={() => setMoving(true)}
            className="shrink-0 rounded-md border border-gray-200 bg-white px-2 py-0.5 text-xs text-gray-600 hover:border-blue-300 hover:bg-blue-50 hover:text-blue-700 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-300 dark:hover:border-blue-500 dark:hover:bg-blue-900/30 dark:hover:text-blue-300"
          >
            {t("workspace:changeFolder")}
          </button>
          <FolderChips
            roomName={currentRoom?.name ?? ""}
            crumbs={folderCrumbs}
            onSelect={openFolderInList}
          />
        </div>
      )}
```

Then in `apps/web/src/components/detail/FolderChips.tsx`, remove `-mt-1` from the nav's className and update the comment above it. Change:

```jsx
  // -mt-1 counteracts the hero's space-y-2.5 so the path sits tight under the name as part of the title
  // block, rather than floating between the name and the subtitle. The subtitle carries the same pull.
  return (
    <nav aria-label={t("folderChipsLabel")} className="-mt-1 flex flex-wrap items-center gap-1">
```

to:

```jsx
  // No top margin of its own: this renders inside the detail page's folder row, which owns the pull that
  // keeps the row tight under the recording name (the subtitle below carries the same pull).
  return (
    <nav aria-label={t("folderChipsLabel")} className="flex flex-wrap items-center gap-1">
```

`t` is already in scope in `RecordingDetail` (`const { t } = useTranslation(...)`); the page namespaces its keys explicitly as `t("workspace:...")`, matching the neighbouring subtitle.

- [ ] **Step 5: Run the tests and verify they pass**

```bash
cd apps/web && npm test -- RecordingDetail
```

Expected: PASS. Pay particular attention to the pre-existing chip tests - "shows the room and the folder chain the recording is filed in", "sits directly under the name, above the source and date line", and the drill tests must all still pass. If "keeps the button outside the breadcrumb navigation landmark" fails, the button was nested inside `FolderChips` rather than the wrapper.

- [ ] **Step 6: Run the full web suite**

```bash
cd apps/web && npm test
```

Expected: all green, no new warnings. `FolderChips` has one consumer, but the whole suite confirms nothing else depended on its margin.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/pages/RecordingDetail.tsx apps/web/src/components/detail/FolderChips.tsx apps/web/src/pages/RecordingDetail.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(web): add a Change folder button beside the recording's breadcrumbs"
```

---

### Task 4: Verify the layout in the running app

jsdom computes no geometry, so nothing in Task 3 proves the button actually sits to the left of the chips, is vertically aligned with them, or that the row wraps sanely. Those are visual claims and need a real browser. Do not skip this and do not report the feature as done on a green suite alone.

**Files:** none changed unless a defect is found.

**Interfaces:**
- Consumes: the completed Task 3 UI.
- Produces: a screenshot for the PR, and confirmation the row is correct in both themes.

- [ ] **Step 1: Start the dev server via the preview tool**

Use the `preview_start` tool with the `apps/web` dev server (`npm run dev`, port 5173) - do **not** launch it with Bash. If `.claude/launch.json` has no entry for it, add one:

```json
{
  "version": "0.0.1",
  "configurations": [
    { "name": "web", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev"], "port": 5173 }
  ]
}
```

The dev server proxies `/api` and `/hubs` to `:8080`, so the API stack must be reachable for a recording to load. If it is not running, bring up the Docker stack from `deploy/` first.

- [ ] **Step 2: Open a recording that is filed in a folder**

Navigate to a recording that has a folder placement in the room being viewed - the button and chips only render for one that does. If no such recording exists, move one into a folder first via the kebab menu.

- [ ] **Step 3: Confirm the layout**

Take a screenshot and check, by measurement rather than by eye where it matters:

- The button renders to the **left** of the chip row, on the same line.
- Button and chips are vertically centred on each other. Compare `getBoundingClientRect()` mid-points via the `javascript_tool`, rather than judging from the image - a two-pixel baseline drift is invisible in a screenshot and obvious on a real screen.
- The row still sits tight under the recording name, above the source/date subtitle, with no gap opened up by the `-mt-1` move.
- With a deep folder path, the row wraps without the button being orphaned or the page scrolling horizontally.

- [ ] **Step 4: Confirm both themes**

Use `resize_window` with `colorScheme: "dark"` and re-check that the button's border and text are legible against the dark background and that it reads as distinct from the chips.

- [ ] **Step 5: Confirm the behaviour end to end**

Click **Change folder**, pick a different folder in the picker, and confirm the breadcrumbs update **without a reload**. This is the one check that exercises Task 1 against a real API rather than a mocked query client - the whole point of the feature.

- [ ] **Step 6: Fix and re-verify, or move on**

If anything is wrong, fix it, re-run `npm test` in `apps/web`, and repeat from Step 3. Keep the screenshot for the PR body. No commit if nothing changed.

---

### Task 5: Version bump and release notes

Per the repo's release rules, every PR ships exactly one release. This is a functional enhancement: minor +1, build reset.

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`

**Interfaces:**
- Consumes: the finished feature from Tasks 1-4.
- Produces: version `0.206.0` across all five files and a matching `RELEASES[0]` entry.

- [ ] **Step 1: Bump the canonical version and all four mirrors**

All five currently read `0.205.3`; every one becomes `0.206.0`.

- `version.json`: `{ "version": "0.206.0" }`
- `apps/web/package.json` line 4: `"version": "0.206.0",`
- `apps/desktop/package.json` line 4: `"version": "0.206.0",`
- `integrations/n8n-nodes-diariz/package.json` line 3: `"version": "0.206.0",`
- `src/Diariz.Api/Diariz.Api.csproj` line 8: `<Version>0.206.0</Version>`

`versionMirrors.test.ts` fails the build if any drifts. The n8n one matters most - it is what npm publishes under, and a published npm version cannot be corrected afterwards.

- [ ] **Step 2: Add the release entry**

Insert as the new first element of `RELEASES` in `apps/web/src/lib/releases.ts`, above the `0.205.3` entry. Leave `pr` as a placeholder for now - Step 5 fills in the real number once the PR exists.

```ts
  {
    version: "0.206.0",
    date: "2026-08-11",
    pr: 0,
    headline: "Change a recording's folder straight from its page",
    summary:
      "The folder path under a recording's name told you where it was filed but gave you no way to " +
      "change it - moving a recording meant finding it in the More actions menu. There is now a Change " +
      "folder button sitting right next to that path, opening the same folder picker. Two things that " +
      "were quietly wrong are fixed with it: the picker now opens with the recording's current folder " +
      "already marked instead of nothing selected, and the folder path updates the moment the move " +
      "lands rather than showing the old folder until you reloaded the page.",
    added: [
      "A Change folder button beside the folder path on a recording's page, opening the folder picker.",
    ],
    fixed: [
      "The folder path under a recording's name now updates as soon as you move it, instead of showing the old folder until a reload.",
      "The folder picker now opens with the recording's current folder marked.",
    ],
  },
```

No `changed` list - nothing existing behaves differently. No `CAPABILITIES` edit, no README / `docs/features.md` edit: moving a recording between folders is an existing, already-documented capability and this surfaces it in a second place rather than adding one. No schema or architecture change, so `docs/Data_Schema.md` and `docs/Overall_Synopsis_of_Platform.md` are untouched. No help-article edit: the behaviour a user relies on is unchanged.

- [ ] **Step 3: Run the tests that police all of this**

```bash
cd apps/web && npm test -- releases versionMirrors
```

Expected: PASS. `releases.test.ts` asserts `RELEASES[0].version` equals `version.json`; `versionMirrors.test.ts` asserts every mirror matches.

- [ ] **Step 4: Run the full web suite and the typecheck**

```bash
cd apps/web && npm test
```

```bash
cd apps/web && npm run build
```

Expected: both green. `npm run build` runs `tsc` and would catch a type error the test run does not.

- [ ] **Step 5: Commit, push, and open the PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.206.0"
```

```bash
git push -u origin feat/change-folder-button-on-recording-header
```

Then open the PR with `gh pr create`. The body must state the deployment surface explicitly: **server redeploy only, no desktop release** (nothing under `apps/desktop/src/**`, `apps/desktop/build/**`, `electron-builder.config.js` or desktop dependencies was touched; the version bump to `apps/desktop/package.json` is the lockstep mirror only). Include the screenshot from Task 4.

Once `gh pr create` returns the PR number, set `pr:` in the `0.206.0` release entry to that number, then:

```bash
git add apps/web/src/lib/releases.ts
git commit -m "chore(release): record the PR number for 0.206.0"
git push
```

Do **not** guess the number as "the last one plus one" - issues and Dependabot share the same sequence, and no test catches a wrong value.

---

## Self-Review

**Spec coverage:**

| Spec requirement | Task |
|---|---|
| Button left of the breadcrumbs, inside the `folderPlacement` guard | 3 |
| Rendered by `RecordingDetail`, outside the nav landmark | 3 (asserted by its third test) |
| Text-only, no icon, `rounded-md` against the chips' pills | 3 |
| Not `ToolbarButton` (needs a visible label) | 3 |
| Reuses `setMoving` / the existing modal | 3 |
| `-mt-1` moves to the wrapper; `FolderChips` comment updated | 3 |
| Breadcrumb refresh on move and create-and-move | 1 |
| `currentSectionId` passed, `?? null` semantics | 2 |
| Click-to-apply preserved, kebab item kept | Unchanged by design - no task needed; Task 2's test opens the picker *through* the kebab, which pins it as still present |
| i18n key in four locales | 3, Step 1 |
| 0.206.0 + four mirrors + release entry | 5 |
| No README / features.md / CAPABILITIES / schema / architecture / help edits | 5, Step 2 (stated explicitly) |
| Layout claims verified in a browser, not jsdom | 4 |
| Deployment surface stated in the PR | 5, Step 5 |

**Placeholder scan:** no TBD/TODO, no "add error handling", no "similar to Task N". Every code step carries real code. The one deliberate placeholder is `pr: 0`, which Task 5 Step 5 replaces with the real number and explains why it cannot be known earlier.

**Type consistency:** `folderPlacement?.sectionId ?? null` matches `MoveToSectionModal`'s `currentSectionId?: string | null`. `recordingId` is an existing prop, used unchanged in Task 1. `t("workspace:changeFolder")` matches the key added in Task 3 Step 1 across all four catalogs. Helper names are consistent: `renderModalWithClient` / `invalidatedKeys` in Task 1, `renderInRoom` / `inRoom` reused from the existing test file in Tasks 2-3.

**Correction made during review:** Task 2's test originally asserted `aria-current="true"` on `"Select Customers"` for a recording filed in the nested `acme` folder. That would have failed even with the fix correctly applied - `FolderPicker` mounts at the top level and deliberately does not drill to reveal a nested selection, surfacing a "Selected: {{path}}" line instead (`folderPickerCurrentSelection`, `FolderPicker.tsx:165-169`). Task 2 now covers all three shapes the picker uses to mark a current folder: top-level (`aria-current`), nested (the selected-path line), and `null` (the always-rendered "Ungrouped" root row).

**Remaining risk:** the exact row labels (`"Select Customers"`, `"Select Ungrouped"`) follow the idiom the existing `MoveToSectionModal.test.tsx` already relies on (`"Select Work"`, `"Select Ungrouped"`), so they are pattern-confirmed rather than guessed. If one still does not match, read the available labels from the `findByLabelText` failure rather than guessing a second time.
