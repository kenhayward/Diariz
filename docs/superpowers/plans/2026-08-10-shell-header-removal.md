# Shell Header Removal (design option 2a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the 80px full-width brand header, extend the meetings panel to the top of the window, move the capture cluster into a 73px bar above the routed content only, split the account menu out of the room switcher, and lay the panel's tabs out horizontally.

**Architecture:** Four independent, individually shippable slices of the same shell. The tab strip changes axis inside `RecordingsPanel` (self-contained). Then the shared `HubPopoverProvider` is hoisted out of `TopBar` into `WorkspaceLayout` so its two consumers - the recorder cluster and the account menu - can live in different subtrees, and the account avatar moves into the room-switcher row. Then `TopBar` is deleted and replaced by a `CaptureBar` rendered at the top of a new content column inside `Workspace`. Finally the tour anchors, help copy, reference docs and release metadata are brought in line.

**Tech Stack:** React 19 + TypeScript + Vite + Tailwind v4, vitest + @testing-library/react (jsdom), i18next.

## Global Constraints

- **TDD is required** (CLAUDE.md). Write the failing test first, run it and see it fail, then write the minimal code to make it pass. No production code without a preceding failing test.
- **No em/en dashes in user-facing text.** Use a plain hyphen `-` in UI strings, i18n catalogs, help articles and release notes. (Code and internal docs are unaffected.)
- **Never commit or push to `main`.** Work on a branch; finish by pushing and opening a PR (`git push -u origin <branch>` + `gh pr create`). Never merge locally.
- **Never `git add -A` in this repo** - it sweeps agent scratch files into the commit. Stage explicit paths only.
- **Help articles are ASCII only** and carry a `title` / `summary` / `group` / `order` front-matter block (`content/help/helpContent.test.ts` enforces this).
- **Do not port the design bundle's HTML or inline styles.** The `.dc.html` files are references. Express everything in the app's own Tailwind utilities and the existing `--hub-*` token layer.
- **No new design tokens.** Everything the design needs already exists in `apps/web/src/index.css`.
- **Version bump + release entry are part of the final task**, not optional. `apps/web/src/lib/versionMirrors.test.ts` and `releases.test.ts` fail the build if any mirror drifts.
- Branch name for this work: `feat/shell-header-removal`.
- Run the web suite with `cd apps/web && npm test`. A single file: `npm test -- src/components/nav/TabStrip.test.tsx`.

**Design source of truth:** the handoff bundle extracted at
`C:\Users\kenha\AppData\Local\Temp\claude\D--Repositories-Diariz\0abceb92-0101-4ff7-b636-a474dd74e90c\scratchpad\header-redesign\design_handoff_shell_header_removal\`
(`README.md`, `screenshots/shell-light.png`, `screenshots/shell-dark.png`). Only `variant="topTabs"` in `DiarizFrame.dc.html` is the approved layout; the other variants are rejected alternatives.

---

## File Structure

| File | Responsibility after this change |
| :--- | :--- |
| `apps/web/src/components/nav/TabStrip.tsx` | **Modify.** Horizontal List / Calendar / Actions / Tags strip, styled exactly like `DetailTabs.tsx`'s strip. `role="tablist"` + `role="tab"` + `aria-selected`. |
| `apps/web/src/components/nav/TabStrip.test.tsx` | **Create.** Unit test for the strip's roles, selected state and callback. |
| `apps/web/src/components/RecordingsPanel.tsx` | **Modify.** Render the strip above the tab body instead of beside it. |
| `apps/web/src/components/UserMenu.tsx` | **Modify.** Trigger becomes the nav-row avatar pill (`size="xs"` + a caret); popover anchors `left-0`. |
| `apps/web/src/components/RoomSwitcher.tsx` | **Modify.** Takes an optional `leading` slot rendered first in the row; the room name keeps its own trigger and caret. |
| `apps/web/src/components/Workspace.tsx` | **Modify.** Passes `<UserMenu />` as the room row's `leading`; wraps `<main>` in a content column with `<CaptureBar />` above it. |
| `apps/web/src/components/WorkspaceLayout.tsx` | **Modify.** Drops `<TopBar />`; hosts `HubPopoverProvider` around `<Workspace />` and mounts `<ThemeSync />`. |
| `apps/web/src/components/hub/CaptureBar.tsx` | **Create.** The 73px capture bar: two flex spacers around the `data-tour="capture"` recorder cluster. |
| `apps/web/src/components/hub/CaptureBar.test.tsx` | **Create.** Replaces `hub/TopBar.test.tsx`. |
| `apps/web/src/components/TopBar.tsx` | **Delete.** |
| `apps/web/src/components/hub/TopBar.test.tsx` | **Delete** (superseded by `CaptureBar.test.tsx`). |
| `apps/web/src/components/hub/TopBarIntegration.test.tsx` | **Rename** to `hub/HubIntegration.test.tsx`; content unchanged apart from the describe name. |
| `apps/web/src/index.css` | **Modify.** Remove the now-unused `--hub-bar-border-top` (both themes). |
| `apps/web/src/content/help/en/what-is-diariz.md` | **Modify.** "account menu (top right)" is no longer true. |
| `docs/Overall_Synopsis_of_Platform.md` | **Modify.** One line: the recorder is mounted in the capture bar, not the top bar. |
| `version.json` + 4 mirrors, `apps/web/src/lib/releases.ts` | **Modify.** 0.200.2 -> 0.201.0 and one release entry. |

**Not changed, deliberately:** `Recorder.tsx` (it is self-contained - its root is already `relative` and it anchors its own popovers), `apps/web/public/logo.png` (still used by `EmptyDetail.tsx` and the About modal - do not delete it), `apps/desktop/**` (the design confirms no draggable title strip is needed; the OS title bar handles window dragging).

---

### Task 1: Horizontal panel tab strip

Turn the 28px vertical rail into a horizontal strip above the tab body, adopting `DetailTabs.tsx`'s styling so the app has one horizontal-tab language. The panel's ARIA also changes from `aria-pressed` on plain buttons to `role="tab"` + `aria-selected`, which is what `role="tablist"` requires and what `DetailTabs` already does - so the existing `getByRole("button", { name: "Actions", pressed: false })` queries in `RecordingsPanel.test.tsx` must move to `getByRole("tab", ...)`.

**Files:**
- Create: `apps/web/src/components/nav/TabStrip.test.tsx`
- Modify: `apps/web/src/components/nav/TabStrip.tsx` (whole file)
- Modify: `apps/web/src/components/RecordingsPanel.tsx:272-274` and its closing `</div>` at line 388
- Modify: `apps/web/src/components/RecordingsPanel.test.tsx` lines 178-184, 207, 208, 238, 250, 965, 978, 1175

**Interfaces:**
- Consumes: `PanelTab` from `apps/web/src/lib/panelTab` (`"list" | "calendar" | "actions" | "tags"`), the `workspace` i18n catalog keys `tabList` / `tabCalendar` / `tabActions` / `tabTags`.
- Produces: `TabStrip` keeps its existing default-export signature `({ tab, onSelect }: { tab: PanelTab; onSelect: (t: PanelTab) => void })`. Later tasks do not touch it.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/nav/TabStrip.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import TabStrip from "./TabStrip";

describe("TabStrip", () => {
  it("renders the four panel tabs in a tablist", () => {
    render(<TabStrip tab="list" onSelect={() => {}} />);
    expect(screen.getByRole("tablist")).toBeTruthy();
    expect(screen.getAllByRole("tab").map((el) => el.textContent)).toEqual([
      "List",
      "Calendar",
      "Actions",
      "Tags",
    ]);
  });

  it("marks only the active tab as selected", () => {
    render(<TabStrip tab="actions" onSelect={() => {}} />);
    expect(screen.getByRole("tab", { name: "Actions" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByRole("tab", { name: "List" }).getAttribute("aria-selected")).toBe("false");
  });

  it("reports the picked tab", () => {
    const onSelect = vi.fn();
    render(<TabStrip tab="list" onSelect={onSelect} />);
    fireEvent.click(screen.getByRole("tab", { name: "Calendar" }));
    expect(onSelect).toHaveBeenCalledWith("calendar");
  });

  // The strip is a row above the content now, not a rail beside it: it must not carry the vertical
  // writing mode that made the old rail's labels read bottom-to-top.
  it("lays the tabs out horizontally", () => {
    render(<TabStrip tab="list" onSelect={() => {}} />);
    expect(screen.getByRole("tablist").className).toContain("flex");
    expect(screen.getByRole("tab", { name: "List" }).className).not.toContain("writing-mode");
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/web && npm test -- src/components/nav/TabStrip.test.tsx
```

Expected: FAIL - `Unable to find an accessible element with the role "tablist"` (the current strip is a plain `<div>` of `aria-pressed` buttons).

- [ ] **Step 3: Rewrite TabStrip horizontally**

Replace the whole of `apps/web/src/components/nav/TabStrip.tsx` with:

```tsx
import { useTranslation } from "react-i18next";
import type { PanelTab } from "../../lib/panelTab";

/// Horizontal List / Calendar / Actions / Tags tabs, between the list toolbar and the active tab's body.
/// Styling is deliberately identical to DetailTabs' strip so the app has one horizontal-tab language;
/// keep the two in step if either changes.
function TabStrip({
  tab,
  onSelect,
}: {
  tab: PanelTab;
  onSelect: (t: PanelTab) => void;
}) {
  const { t } = useTranslation("workspace");
  const item = (key: PanelTab, label: string) => {
    const isActive = tab === key;
    return (
      <button
        type="button"
        role="tab"
        aria-selected={isActive}
        onClick={() => onSelect(key)}
        className={`-mb-px border-b-2 px-3 py-2 text-[12.5px] ${
          isActive
            ? "border-gray-900 font-medium text-gray-900 dark:border-gray-100 dark:text-gray-100"
            : "border-transparent text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
        }`}
      >
        {label}
      </button>
    );
  };
  return (
    <div role="tablist" className="flex shrink-0 gap-1 border-b px-1.5 dark:border-gray-700">
      {item("list", t("tabList"))}
      {item("calendar", t("tabCalendar"))}
      {item("actions", t("tabActions"))}
      {item("tags", t("tabTags"))}
    </div>
  );
}
export default TabStrip;
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd apps/web && npm test -- src/components/nav/TabStrip.test.tsx
```

Expected: PASS (4 tests).

- [ ] **Step 5: Move the strip above the tab body in RecordingsPanel**

In `apps/web/src/components/RecordingsPanel.tsx`, the strip is currently the first child of the flex row that holds the tab body (lines 272-274):

```tsx
      <div className="flex min-h-0 flex-1">
        <TabStrip tab={tab} onSelect={selectTab} />
        {tab === "list" ? (
```

Change it to render above that row:

```tsx
      <TabStrip tab={tab} onSelect={selectTab} />
      <div className="flex min-h-0 flex-1">
        {tab === "list" ? (
```

Nothing else in the file moves - the closing `</div>` of the flex row (line 388, just before `{editingAction && ...}`) stays where it is.

- [ ] **Step 6: Update the RecordingsPanel test queries to the tab role**

In `apps/web/src/components/RecordingsPanel.test.tsx`:

Lines 178-184 - the kebab helper's comment about disambiguating from the tab is now stale, and the tab no longer matches `role="button"` at all. Replace the helper and its comment with:

```tsx
/// Open a recording row's kebab. Its aria-label is "Actions" (KebabMenu's default); the "Actions" panel
/// tab is a `role="tab"`, so a button-role query no longer collides with it.
function openKebab() {
  fireEvent.click(screen.getByRole("button", { name: /actions/i }));
}
```

Then rewrite these six queries:

| Line | Before | After |
| :--- | :--- | :--- |
| 207 | `screen.getByRole("button", { name: /^tags$/i })` | `screen.getByRole("tab", { name: "Tags" })` |
| 208 | `screen.getByRole("button", { name: "Actions", pressed: false })` | `screen.getByRole("tab", { name: "Actions" })` |
| 238 | `fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }))` | `fireEvent.click(screen.getByRole("tab", { name: "Actions" }))` |
| 250 | `fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }))` | `fireEvent.click(screen.getByRole("tab", { name: "Actions" }))` |
| 965 | `fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }))` | `fireEvent.click(screen.getByRole("tab", { name: "Actions" }))` |
| 978 | `fireEvent.click(screen.getByRole("button", { name: "Actions", pressed: false }))` | `fireEvent.click(screen.getByRole("tab", { name: "Actions" }))` |
| 1175 | `fireEvent.click(screen.getByRole("button", { name: /calendar/i }))` | `fireEvent.click(screen.getByRole("tab", { name: "Calendar" }))` |

The comment above line 965 (`// Switch to the Actions tab (the vertical tab carries aria-pressed; the row kebab is also "Actions").`) is now wrong. Replace it with:

```tsx
    // Switch to the Actions tab (a role="tab"; the row kebab is a button also labelled "Actions").
```

- [ ] **Step 6b: Lock the strip's position above the search field**

The design puts the tabs above the search field, and `SearchBar` lives inside the List tab's body - so moving the strip out of the flex row is what puts it there. Add this to `apps/web/src/components/RecordingsPanel.test.tsx`, inside `describe("RecordingsPanel", ...)`:

```tsx
  // The strip is a row across the top of the panel now: toolbar, tabs, then the tab's own body (which for
  // List starts with the search field). Order, not just presence - a strip rendered below the search would
  // still satisfy a "renders the tabs" assertion.
  it("renders the tab strip above the search field", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    const strip = screen.getByRole("tablist");
    const search = screen.getByPlaceholderText(/search/i);
    expect(strip.compareDocumentPosition(search) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
```

If `getByPlaceholderText(/search/i)` does not match, open `apps/web/src/components/nav/SearchBar.tsx` and query the input the way `nav/SearchBar.test.tsx` already does - do not weaken the assertion to a presence check.

- [ ] **Step 7: Run the panel suite and see it pass**

```bash
cd apps/web && npm test -- src/components/RecordingsPanel.test.tsx src/components/nav/TabStrip.test.tsx
```

Expected: PASS. If a query is still ambiguous, the failure message names the competing elements - fix the query, do not weaken the assertion.

- [ ] **Step 8: Run the full web suite and the typecheck**

```bash
cd apps/web && npm test && npm run build
```

Expected: PASS, with no new warnings. `npm run build` runs `tsc` and must be clean.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/components/nav/TabStrip.tsx apps/web/src/components/nav/TabStrip.test.tsx apps/web/src/components/RecordingsPanel.tsx apps/web/src/components/RecordingsPanel.test.tsx
git commit -m "feat(web): lay the panel tabs out horizontally above the tab body"
```

---

### Task 2: Move the account menu into the room-switcher row

The account avatar leaves the header and becomes a pill at the left of the room row. Its popover state (`useHubPopover("acct")`) is shared with the recorder's popovers, so the `HubPopoverProvider` must be hoisted out of `TopBar` first - after this task the recorder cluster (still in `TopBar`) and the account menu (now in the left panel) are in different subtrees and must still enforce one-open-at-a-time. `ThemeSync` moves with it, because it is currently only mounted by `TopBar`.

`RoomSwitcher` gets an optional `leading` slot rather than importing `UserMenu` itself: `RoomSwitcher.test.tsx` mocks `react-router-dom` down to `useNavigate` alone, and `UserMenu` needs react-query, `useAuth` and `useTour` - a direct import would drag all of that into a test that is about room switching.

**Files:**
- Modify: `apps/web/src/components/UserMenu.tsx:92-117` (trigger markup + popover anchor)
- Modify: `apps/web/src/components/RoomSwitcher.tsx:23,53-55` (new `leading` prop, row spacing)
- Modify: `apps/web/src/components/Workspace.tsx:82` (pass `leading={<UserMenu />}`)
- Modify: `apps/web/src/components/WorkspaceLayout.tsx` (hoist `HubPopoverProvider` + `ThemeSync`)
- Modify: `apps/web/src/components/TopBar.tsx` (drop `<UserMenu />`, `HubPopoverProvider`, `ThemeSync`)
- Modify: `apps/web/src/components/RoomSwitcher.test.tsx` (new test for the slot)
- Modify: `apps/web/src/components/Workspace.test.tsx` (stub `UserMenu`, assert placement)
- Modify: `apps/web/src/components/hub/TopBar.test.tsx` (drop the avatar assertion)

**Interfaces:**
- Consumes: `HubPopoverProvider` from `apps/web/src/components/hub/hubPopovers` (already exported); `Avatar` with `size="xs"` (24px, already supported).
- Produces:
  - `RoomSwitcher` default export signature becomes `({ onCollapse, chevron, leading }: { onCollapse: () => void; chevron: string; leading?: ReactNode })`. `leading` is rendered as the first child of the row.
  - `UserMenu` keeps its zero-prop default-export signature and its accessible name `"Account"`; only its trigger markup and popover anchor change.

- [ ] **Step 1: Write the failing tests**

(a) In `apps/web/src/components/RoomSwitcher.test.tsx`, add this test inside the existing `describe("RoomSwitcher", ...)` block, after the `"shows the current room's name..."` test:

```tsx
  // The account menu now lives at the left of this row. The switcher does not own it - it takes it as a
  // slot - so the two triggers stay separately labelled and separately hoverable.
  it("renders a leading slot before the room trigger", () => {
    render(
      <RoomSwitcher onCollapse={() => {}} chevron="◀" leading={<button type="button">ACCOUNT</button>} />,
    );
    const slot = screen.getByText("ACCOUNT");
    const roomTrigger = screen.getByRole("button", { name: /switch room/i });
    expect(slot).toBeTruthy();
    // Precedes the room trigger in document order.
    expect(slot.compareDocumentPosition(roomTrigger) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
```

(b) In `apps/web/src/components/Workspace.test.tsx`, add the stub next to the existing mocks at the top (after the `ChatPanel` mock):

```tsx
// The account menu is a real component with react-query + auth dependencies; this test is about the shell.
vi.mock("./UserMenu", () => ({
  default: () => <button data-tour="account" type="button">ACCOUNT</button>,
}));
```

and add this test inside `describe("Workspace", ...)`:

```tsx
  it("puts the account menu in the left panel's room row, next to the room switcher", () => {
    renderWorkspace();
    const account = screen.getByRole("button", { name: "ACCOUNT" });
    const collapse = screen.getByRole("button", { name: /collapse personal panel/i });
    // Same row: the collapse chevron is the row's last control, so they share a parent element.
    expect(account.parentElement).toBe(collapse.parentElement);
  });
```

(c) In `apps/web/src/components/hub/TopBar.test.tsx`, the avatar no longer belongs to the bar. Replace the last test:

```tsx
  it("mounts the account avatar (UserMenu)", () => {
    const { container } = renderBar();
    expect(container.querySelector('[data-tour="account"]')).toBeTruthy();
  });
```

with:

```tsx
  // The account menu moved into the left panel's room row - the bar must not carry a second copy.
  it("does not mount the account avatar", () => {
    const { container } = renderBar();
    expect(container.querySelector('[data-tour="account"]')).toBeNull();
  });
```

and delete the now-unused `vi.mock("../UserMenu", ...)` block at the top of that file (lines 11-17).

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd apps/web && npm test -- src/components/RoomSwitcher.test.tsx src/components/Workspace.test.tsx src/components/hub/TopBar.test.tsx
```

Expected: FAIL in all three - RoomSwitcher: `Unable to find an element with the text: ACCOUNT` (no `leading` prop yet); Workspace: same; TopBar: `expected <button> to be null` (the avatar is still in the bar).

- [ ] **Step 3: Give RoomSwitcher a leading slot**

In `apps/web/src/components/RoomSwitcher.tsx`, add `ReactNode` to the React import on line 1 and take the new prop:

```tsx
import { useEffect, useRef, useState, type ReactNode } from "react";
```

Change the signature on line 23 and the doc comment above it:

```tsx
/// The left-panel header row: an optional leading slot (the account menu), a room switcher (current room's
/// icon + name, a dropdown of the rooms the user belongs to, personal first) and the panel collapse control.
/// The slot is a prop rather than an import so this component stays free of the account menu's dependencies.
export default function RoomSwitcher({
  onCollapse,
  chevron,
  leading,
}: {
  onCollapse: () => void;
  chevron: string;
  leading?: ReactNode;
}) {
```

Then render the slot first in the row and widen the gap to the design's 1.5 (line 54):

```tsx
    <div className="flex h-9 shrink-0 items-center justify-between gap-1.5 border-b px-2 dark:border-gray-700">
      {leading}
      <div className="relative min-w-0 flex-1" ref={ref}>
```

- [ ] **Step 4: Restyle the UserMenu trigger as the nav pill**

In `apps/web/src/components/UserMenu.tsx`, replace the trigger button (lines 93-115) with the pill:

```tsx
      {/* The nav-row account pill: a 24px avatar plus its own caret. Its hover treatment is separate from
          the room switcher's next to it - one shared hover across both would read as a single button. */}
      <button
        type="button"
        aria-label="Account"
        aria-haspopup="menu"
        aria-expanded={open}
        data-tour="account"
        onClick={() => toggle("acct")}
        className="flex shrink-0 items-center gap-[3px] rounded-full border border-gray-200 bg-gray-100 py-0.5 pl-0.5 pr-1 hover:border-gray-300 dark:border-gray-700 dark:bg-white/[0.06] dark:hover:border-gray-600"
      >
        <Avatar initials={initials} pictureUrl={pictureUrl} size="xs" />
        <span aria-hidden="true" className="text-[9px] text-gray-400">
          ▾
        </span>
      </button>
```

and re-anchor the popover on line 117 - it used to hang off the right edge of the header, it now opens below and to the right of a trigger at the far left of the panel:

```tsx
      <HubPopover open={open} onClose={close} width={308} anchorClassName="left-0" ariaLabel="Account">
```

Update the component's doc comment (lines 62-67) so it stops describing a 46px header avatar:

```tsx
/**
 * The account menu: a small avatar pill in the left panel's room row that toggles a 308px account popover
 * (header, usage stats, menu rows, Sign out). The pill shares the hub's single-open popover state
 * (`useHubPopover`, id "acct") so opening it closes the recorder popovers in the capture bar and
 * vice-versa; `HubPopover` owns the backdrop + Escape.
 */
```

- [ ] **Step 5: Hoist the popover provider and ThemeSync, and pass the slot**

`apps/web/src/components/TopBar.tsx` - remove the account avatar and the two things that must now live above `Workspace`. The file becomes:

```tsx
import { Link } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import Recorder from "./Recorder";

/// Persistent "command hub" bar: brand on the left, the record cluster centered by two flex spacers.
/// The account avatar has moved into the left panel's room row; the shared popover context now lives in
/// WorkspaceLayout so it can span both.
export default function TopBar() {
  const qc = useQueryClient();
  return (
    <header
      className="flex shrink-0 items-center gap-4 bg-[var(--hub-bar-bg)]"
      style={{
        height: 80,
        padding: "0 22px",
        boxSizing: "border-box",
        borderTop: "2px solid var(--hub-bar-border-top)",
        borderBottom: "1px solid var(--hub-bar-border-bottom)",
      }}
    >
      <Link to="/" className="flex shrink-0 items-center" style={{ gap: 12 }}>
        <img src="/logo.png" alt="" style={{ width: 34, height: 34, borderRadius: 9 }} />
        {/* The wordmark collapses to just the mark at very narrow widths; the mark keeps the home link. */}
        <span
          className="hidden text-[var(--hub-text)] sm:inline"
          style={{ fontFamily: "system-ui", fontWeight: 700, fontSize: 21, letterSpacing: "-.01em" }}
        >
          Diariz
        </span>
      </Link>

      <div style={{ flex: 1 }} />

      <div data-tour="capture">
        <Recorder compact onUploaded={() => qc.invalidateQueries({ queryKey: ["recordings"] })} />
      </div>

      <div style={{ flex: 1 }} />
    </header>
  );
}
```

`apps/web/src/components/WorkspaceLayout.tsx` - add the two imports and wrap `Workspace`:

```tsx
import TopBar from "./TopBar";
import Workspace from "./Workspace";
import TourOverlay from "./TourOverlay";
import StatusBar from "./StatusBar";
import ThemeSync from "./ThemeSync";
import OutlookSyncBridge from "./OutlookSyncBridge";
import { HubPopoverProvider } from "./hub/hubPopovers";
import { UploadProvider } from "../lib/uploadContext";
import { TourProvider } from "../lib/tour";
import { StatusProvider } from "../lib/status";
import { RoomProvider } from "../lib/rooms";
import { ToastProvider } from "../lib/toast";
```

and inside the frame:

```tsx
              <div className="flex h-screen flex-col bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
                <TopBar />
                {/* One popover open at a time across the recorder cluster and the account menu. They sit in
                    different subtrees now (the capture bar and the left panel's room row), so the context
                    has to span the whole workspace rather than one bar. */}
                <HubPopoverProvider>
                  <Workspace />
                </HubPopoverProvider>
                <StatusBar />
                {/* Renders nothing. Mounted here rather than in Preferences because a sync fires on launch
                    and from the tray, neither of which opens the settings window. A no-op in a browser. */}
                <OutlookSyncBridge />
                {/* Renders nothing: reconciles the server-persisted theme once signed in. */}
                <ThemeSync />
              </div>
```

`apps/web/src/components/Workspace.tsx` - import `UserMenu` (next to the `RoomSwitcher` import on line 7):

```tsx
import UserMenu from "./UserMenu";
```

and pass it as the row's leading control (line 82):

```tsx
              <RoomSwitcher onCollapse={() => setLeftOpen(false)} chevron="◀" leading={<UserMenu />} />
```

- [ ] **Step 6: Run the three suites and see them pass**

```bash
cd apps/web && npm test -- src/components/RoomSwitcher.test.tsx src/components/Workspace.test.tsx src/components/hub/TopBar.test.tsx src/components/UserMenu.test.tsx
```

Expected: PASS. `UserMenu.test.tsx` queries only by the accessible name `/account/i`, so the restyle must not change its results - if it does, the accessible name regressed.

- [ ] **Step 7: Run the full web suite and the typecheck**

```bash
cd apps/web && npm test && npm run build
```

Expected: PASS, clean.

- [ ] **Step 8: Verify the cross-subtree popover in the running app**

Start the dev server via the preview tooling (not Bash), sign in, then: open the audio-source popover from the recorder cluster, click the account pill, and confirm the source popover closes and the account menu opens (and the reverse). The `hub/TopBarIntegration.test.tsx` covers this at unit level, but it renders both components under one provider by hand - this checks the real tree now that they are in different subtrees.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/components/UserMenu.tsx apps/web/src/components/RoomSwitcher.tsx apps/web/src/components/RoomSwitcher.test.tsx apps/web/src/components/Workspace.tsx apps/web/src/components/Workspace.test.tsx apps/web/src/components/WorkspaceLayout.tsx apps/web/src/components/TopBar.tsx apps/web/src/components/hub/TopBar.test.tsx
git commit -m "feat(web): move the account menu into the left panel's room row"
```

---

### Task 3: Delete the brand header, add the capture bar

The header goes; the recorder cluster becomes a 73px bar at the top of a new content column, spanning the routed content only - not the left panel, not the chat rail.

The bar's height is load-bearing: **73px + its 1px bottom border = 74px**, which is exactly the left panel's first two rows (`h-9` = 36px + 1px border, twice). The bar's lower edge lands on the panel's second divider. If those row heights ever change, this changes with them.

**Files:**
- Create: `apps/web/src/components/hub/CaptureBar.tsx`
- Create: `apps/web/src/components/hub/CaptureBar.test.tsx`
- Delete: `apps/web/src/components/TopBar.tsx`, `apps/web/src/components/hub/TopBar.test.tsx`
- Rename: `apps/web/src/components/hub/TopBarIntegration.test.tsx` -> `apps/web/src/components/hub/HubIntegration.test.tsx`
- Modify: `apps/web/src/components/WorkspaceLayout.tsx` (drop `<TopBar />` and its import)
- Modify: `apps/web/src/components/Workspace.tsx:100-107` (content column)
- Modify: `apps/web/src/components/Workspace.test.tsx` (stub `CaptureBar`, assert the column)
- Modify: `apps/web/src/index.css:12,45` (remove `--hub-bar-border-top`)

**Interfaces:**
- Consumes: `Recorder` (`compact` + `onUploaded` props, unchanged), `useQueryClient` from `@tanstack/react-query`.
- Produces: `CaptureBar` - a zero-prop default export rendering a `shrink-0` bar whose only content is the `data-tour="capture"` cluster between two flex spacers.

- [ ] **Step 1: Write the failing tests**

(a) Create `apps/web/src/components/hub/CaptureBar.test.tsx`:

```tsx
import { render } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi } from "vitest";
import CaptureBar from "./CaptureBar";

// Keep this a shell test: stub the recorder so we only assert the bar frame + regions.
vi.mock("../Recorder", () => ({
  default: () => <div data-testid="recorder-stub">recorder</div>,
}));

function renderBar() {
  const qc = new QueryClient();
  return render(
    <QueryClientProvider client={qc}>
      <CaptureBar />
    </QueryClientProvider>,
  );
}

describe("CaptureBar", () => {
  it("mounts the Recorder inside the capture cluster", () => {
    const { container } = renderBar();
    const cluster = container.querySelector('[data-tour="capture"]');
    expect(cluster).toBeTruthy();
    expect(cluster?.querySelector('[data-testid="recorder-stub"]')).toBeTruthy();
  });

  // The brand block is gone: the browser tab already carries the icon and the name.
  it("carries no brand mark or wordmark", () => {
    const { container, queryByText } = renderBar();
    expect(container.querySelector('img[src="/logo.png"]')).toBeNull();
    expect(queryByText("Diariz")).toBeNull();
  });

  // 73px + the 1px bottom border = 74px = the left panel's first two rows. There is no window edge above
  // the bar any more, so the old 2px top border goes with the header.
  it("is 73px tall with a bottom border and no top border", () => {
    const { container } = renderBar();
    const bar = container.firstElementChild as HTMLElement;
    expect(bar.style.height).toBe("73px");
    expect(bar.style.borderBottom).toContain("var(--hub-bar-border-bottom)");
    expect(bar.style.borderTop).toBe("");
  });
});
```

(b) In `apps/web/src/components/Workspace.test.tsx`, add the stub next to the other mocks:

```tsx
vi.mock("./hub/CaptureBar", () => ({
  default: () => <div data-tour="capture">CAPTURE</div>,
}));
```

and add these two tests inside `describe("Workspace", ...)`:

```tsx
  // The bar spans the routed content only: it shares a column with <main>, and the chat rail is outside
  // that column so the bar never runs over it.
  it("renders the capture bar above the routed content, inside the content column", () => {
    renderWorkspace("/recordings/rec-1");
    const bar = screen.getByText("CAPTURE");
    const main = document.querySelector("main")!;
    expect(bar.parentElement).toBe(main.parentElement);
    expect(bar.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("keeps the chat rail out of the capture bar's column", () => {
    renderWorkspace();
    const column = screen.getByText("CAPTURE").parentElement!;
    const rail = screen.getByRole("button", { name: /expand chat panel/i });
    expect(column.contains(rail)).toBe(false);
  });
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd apps/web && npm test -- src/components/hub/CaptureBar.test.tsx src/components/Workspace.test.tsx
```

Expected: FAIL - `Failed to resolve import "./CaptureBar"` for the first file, and `Unable to find an element with the text: CAPTURE` for Workspace.

- [ ] **Step 3: Create the CaptureBar**

Create `apps/web/src/components/hub/CaptureBar.tsx`:

```tsx
import { useQueryClient } from "@tanstack/react-query";
import Recorder from "../Recorder";

/// The capture bar: the record cluster centred over the content column by two flex spacers. It sits above
/// the routed content and spans that column only - not the left panel, not the chat rail.
///
/// 73px + its 1px bottom border = 74px, which is exactly the left panel's first two rows (h-9 + border,
/// twice), so the bar's lower edge lands on the panel's second divider. That alignment is the point: if
/// those row heights change, change this with them. There is no window edge above the bar any more, so it
/// carries no top border (the header's 2px `--hub-bar-border-top` went with the header).
export default function CaptureBar() {
  const qc = useQueryClient();
  return (
    <div
      className="flex shrink-0 items-center gap-4 bg-[var(--hub-bar-bg)]"
      style={{
        height: 73,
        padding: "0 18px",
        boxSizing: "border-box",
        borderBottom: "1px solid var(--hub-bar-border-bottom)",
      }}
    >
      <div style={{ flex: 1 }} />

      <div data-tour="capture">
        <Recorder compact onUploaded={() => qc.invalidateQueries({ queryKey: ["recordings"] })} />
      </div>

      <div style={{ flex: 1 }} />
    </div>
  );
}
```

- [ ] **Step 4: Wrap `<main>` in a content column**

In `apps/web/src/components/Workspace.tsx`, add the import next to the others:

```tsx
import CaptureBar from "./hub/CaptureBar";
```

and replace the `<main>` block (lines 100-107) with the column:

```tsx
      {/* The content column: the capture bar over the routed content. The chat rail is a sibling of this
          column, not a child, so the bar stops at the content's right edge. */}
      <div className="flex min-w-0 flex-1 flex-col">
        <CaptureBar />
        <main
          data-tour="detail"
          className="min-h-0 min-w-0 flex-1 overflow-y-auto bg-gray-50 dark:bg-gray-950"
        >
          <div className="p-6">
            {/* Contain a routed-page crash so it shows a message instead of blanking the whole app (#289). */}
            <RouteErrorBoundary>
              <Outlet />
            </RouteErrorBoundary>
          </div>
        </main>
      </div>
```

(`min-h-0` is new and required: `<main>` is now a flex **column** child, and without it the content cannot shrink below its intrinsic height, so the page scrolls instead of the panel.)

- [ ] **Step 5: Remove the header from the layout**

In `apps/web/src/components/WorkspaceLayout.tsx`, delete the `import TopBar from "./TopBar";` line and the `<TopBar />` element, and update the file's doc comment (lines 12-16) - it describes a bar that no longer exists:

```tsx
/// Full-height app frame: the three-panel workspace with a status bar locked to the bottom (a shrink-0 flex
/// child, so it never scrolls while the panels scroll internally). There is no brand header - the meetings
/// panel runs to the top of the window and the capture bar sits inside the content column (Workspace.tsx).
/// UploadProvider spans the workspace so the capture bar's Upload button and the recordings drop zone share
/// one queue. StatusProvider spans the workspace + status bar so routed pages can push progress messages the
/// bar shows. HubPopoverProvider spans the workspace so the capture bar and the account menu share one open
/// popover. TourProvider drives the first-run guided tour (TourOverlay renders on top when active).
```

- [ ] **Step 6: Delete the old header and its test, rename the integration test**

```bash
git rm apps/web/src/components/TopBar.tsx apps/web/src/components/hub/TopBar.test.tsx
git mv apps/web/src/components/hub/TopBarIntegration.test.tsx apps/web/src/components/hub/HubIntegration.test.tsx
```

In the renamed file, the two describe names still say "TopBar". Change:
- `describe("TopBar command-hub integration", ...)` -> `describe("command-hub integration", ...)`
- the comment on line 147 - `// The full command hub: the recorder cluster and the account avatar under one shared popover context - the same wiring TopBar uses (minus the pure frame markup, which TopBar.test covers).` -> `// The full command hub: the capture bar's recorder cluster and the left panel's account pill under one shared popover context - the same wiring WorkspaceLayout provides.`

Also fix the two stale `TopBar` references in `apps/web/src/components/Recorder.tsx` (comments only, no behaviour): line ~1123 `the TopBar is a` -> `the capture bar is a`, and line ~1300 `They must stay out of the TopBar's flow: it is a fixed-height header` -> `They must stay out of the capture bar's flow: it is a fixed-height bar`.

- [ ] **Step 7: Drop the unused token**

In `apps/web/src/index.css`, delete line 12 (`--hub-bar-border-top: rgba(47, 107, 237, 0.2);`) and line 45 (`--hub-bar-border-top: rgba(120, 150, 220, 0.18);`). Confirm nothing else reads it:

```bash
cd apps/web && grep -rn "hub-bar-border-top" src/ ; echo "exit=$?"
```

Expected: no matches (`exit=1`).

- [ ] **Step 8: Run the affected suites and see them pass**

```bash
cd apps/web && npm test -- src/components/hub src/components/Workspace.test.tsx
```

Expected: PASS.

- [ ] **Step 9: Run the full web suite and the typecheck**

```bash
cd apps/web && npm test && npm run build
```

Expected: PASS, clean. A dangling `TopBar` import anywhere shows up here as a `tsc` error.

- [ ] **Step 10: Verify the shell in the running app, light and dark**

Start the dev server via the preview tooling and check against `screenshots/shell-light.png` and `screenshots/shell-dark.png`:
1. No header; the left panel starts at the top of the window.
2. The capture bar's bottom edge lines up with the left panel's **second** divider (below the list toolbar).
3. The bar stops at the chat rail - the rail runs the full height beside it.
4. Collapsing the left panel (`◀`) leaves the 36px rail running the full window height, and the bar simply widens.
5. Start a recording: the idle pill swaps for the recording pill in place, and the notes popover anchors under it.
6. Toggle dark mode and re-check 1-5.

Take a screenshot of each theme and attach both to the PR.

- [ ] **Step 11: Commit**

```bash
git add apps/web/src/components/hub/CaptureBar.tsx apps/web/src/components/hub/CaptureBar.test.tsx apps/web/src/components/hub/HubIntegration.test.tsx apps/web/src/components/Workspace.tsx apps/web/src/components/Workspace.test.tsx apps/web/src/components/WorkspaceLayout.tsx apps/web/src/components/Recorder.tsx apps/web/src/index.css
git commit -m "feat(web): remove the brand header and move capture into the content column"
```

---

### Task 4: Tour anchors, help copy, docs and the release

Both tour anchors moved: `capture` from the header into the content column, `account` from the top right into the left panel. The step targets and copy are unchanged in substance (neither string names a position), but one help article does name a position and is now wrong. This task also carries the mandatory version bump and release entry.

**Files:**
- Create: `apps/web/src/lib/onboarding.test.ts` (if it does not already exist - check first)
- Modify: `apps/web/src/content/help/en/what-is-diariz.md:25`
- Modify: `docs/Overall_Synopsis_of_Platform.md:1522`
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`

**Interfaces:**
- Consumes: `TOUR_STEPS` from `apps/web/src/lib/onboarding` (an array of `{ target: string }`), the `RELEASES` array from `apps/web/src/lib/releases.ts`.
- Produces: nothing other tasks depend on. This is the last task.

- [ ] **Step 1: Write the failing test for the tour anchors**

The tour spotlights whatever carries `data-tour="<target>"`. After the move, both anchors still have to exist in the rendered workspace - the failure mode this guards against is an anchor that was deleted with the header, which would leave the tour dimming the screen with nothing lit.

First check whether the file already exists:

```bash
ls apps/web/src/lib/onboarding.test.ts
```

If it does, add the test below to it. If not, create `apps/web/src/lib/onboarding.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { TOUR_STEPS } from "./onboarding";

describe("TOUR_STEPS", () => {
  // Every step spotlights a `data-tour="…"` region. The capture cluster moved into the capture bar and the
  // account pill into the left panel's room row; both anchors have to survive the header's removal, or the
  // tour dims the app with nothing lit.
  it("targets the regions the shell still renders", () => {
    expect(TOUR_STEPS.map((s) => s.target)).toEqual([
      "capture",
      "recordings",
      "detail",
      "chat",
      "account",
    ]);
  });
});
```

Then add this to `apps/web/src/components/Workspace.test.tsx` (its `CaptureBar` and `UserMenu` stubs from Tasks 2 and 3 already carry the two attributes). Note it iterates `TOUR_STEPS` rather than repeating the five strings - that is what makes the pair a contract: `onboarding.test.ts` pins the list, and this pins that every entry on the list is actually rendered. Add the import at the top of the file:

```tsx
import { TOUR_STEPS } from "../lib/onboarding";
```

and the test inside `describe("Workspace", ...)`:

```tsx
  // The tour spotlights each step's region by attribute, and every step's region lives in the workspace.
  // The capture cluster moved into the capture bar and the account pill into the room row; if either
  // anchor were dropped in the move, the tour would dim the app with nothing lit.
  it("renders a region for every tour step", () => {
    renderWorkspace("/recordings/rec-1");
    for (const step of TOUR_STEPS) {
      expect(document.querySelector(`[data-tour="${step.target}"]`)).toBeTruthy();
    }
  });
```

- [ ] **Step 2: Run the tests**

```bash
cd apps/web && npm test -- src/lib/onboarding.test.ts src/components/Workspace.test.tsx
```

Expected: **PASS** if Tasks 2 and 3 landed correctly - these are regression guards over work already done, not a red-to-green cycle. If either fails, an anchor was lost in the move: fix the component, not the test.

To prove the Workspace assertion can fail, temporarily delete `data-tour="capture"` from the `CaptureBar` stub in that test file, re-run, confirm it reports the missing anchor, then put it back. Do not commit the mutation.

- [ ] **Step 3: Fix the help article**

In `apps/web/src/content/help/en/what-is-diariz.md`, line 25 currently reads:

```markdown
- The **account menu** (top right) holds Preferences, Settings, the guided tour, and this help.
```

Replace it with:

```markdown
- The **account menu** is the small avatar at the top left, above the meetings list. It holds Preferences,
  Settings, the guided tour, and this help.
```

ASCII only, plain hyphens. Leave the article's front matter untouched.

- [ ] **Step 4: Fix the architecture doc**

In `docs/Overall_Synopsis_of_Platform.md` line 1522, `(the recorder is mounted once in the top bar; a plain subscription keeps the page ignorant of where it is)` becomes:

```
(the recorder is mounted once in the capture bar; a plain subscription keeps the page ignorant of where it is)
```

No other doc changes are required: this is a layout change, not a scope change, so the README Features table, `docs/features.md`, the About-box `CAPABILITIES` table and `docs/Data_Schema.md` all stay as they are. Say so explicitly in the PR body.

- [ ] **Step 5: Run the help-content test**

```bash
cd apps/web && npm test -- src/content/help/helpContent.test.ts
```

Expected: PASS (it enforces the ASCII rule and the front-matter block).

- [ ] **Step 6: Bump the version in lockstep**

This is a functional enhancement, so **Minor +1, Build reset to 0**: `0.200.2` -> `0.201.0`. Set `0.201.0` in all five files:

- `version.json` - `{ "version": "0.201.0" }`
- `apps/web/package.json` - `"version": "0.201.0"`
- `apps/desktop/package.json` - `"version": "0.201.0"`
- `src/Diariz.Api/Diariz.Api.csproj` - `<Version>0.201.0</Version>`
- `integrations/n8n-nodes-diariz/package.json` - `"version": "0.201.0"`

- [ ] **Step 7: Add the release entry**

Get the PR number first - guessing "last + 1" is wrong here, because Dependabot PRs and issues share the sequence:

```bash
gh pr list --state all --limit 1 --json number
```

The next PR is that number + 1 **only if** nothing else lands first; confirm it against the PR you open in Step 10 and correct the entry before merging if it differs. Insert at the top of `RELEASES` in `apps/web/src/lib/releases.ts` (replace `<PR>` with the number):

```ts
  {
    version: "0.201.0",
    date: "2026-08-10",
    pr: <PR>,
    headline: "The meetings list now runs to the top of the window",
    summary:
      "The brand bar across the top of the app is gone - the browser tab already carries the icon and the name, so it was spending 80 pixels on saying it twice. The meetings panel now starts at the top of the window, and the record controls sit in their own bar above whatever you have open, so they no longer stretch across the whole app. The account menu moved to the small avatar at the top left of the meetings panel, next to the room name. The panel's List, Calendar, Actions and Tags tabs are laid out along the top of the panel instead of sideways down its left edge, which reads more easily and gives the folder list the full width of the panel.",
    changed: [
      "Removed the brand header; the meetings panel and the capture bar now start at the top of the window.",
      "The record controls sit above the open recording rather than spanning the whole app.",
      "The account menu is now the avatar at the top left of the meetings panel; the room name beside it still switches rooms.",
      "The panel's List / Calendar / Actions / Tags tabs are horizontal, matching the tabs on a recording, and the folder list gets the full panel width.",
    ],
  },
```

Check the exact shape of the neighbouring entry before writing this one and match it (field order, and whether `added` / `fixed` are omitted when empty).

- [ ] **Step 8: Run the release + mirror tests**

```bash
cd apps/web && npm test -- src/lib/releases.test.ts src/lib/versionMirrors.test.ts
```

Expected: PASS. `versionMirrors.test.ts` fails loudly if any of the five files drifted; `releases.test.ts` asserts `RELEASES[0].version === version.json`.

- [ ] **Step 9: Full verification before the PR**

```bash
cd apps/web && npm test && npm run build
```

Expected: PASS with no errors or warnings. Quote the real summary line in the PR body - do not claim green without it.

- [ ] **Step 10: Commit, push and open the PR**

```bash
git add apps/web/src/lib/onboarding.test.ts apps/web/src/components/Workspace.test.tsx apps/web/src/content/help/en/what-is-diariz.md docs/Overall_Synopsis_of_Platform.md docs/superpowers/plans/2026-08-10-shell-header-removal.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "chore(web): retarget the tour/help copy for the new shell and cut 0.201.0"
git push -u origin feat/shell-header-removal
```

Then `gh pr create`. The PR body must state:

- **Deployment surface: server redeploy only.** Nothing under `apps/desktop/src/**`, `apps/desktop/build/**`, `electron-builder.config.js` or the desktop dependencies changed - the desktop shell loads the web app from the server origin, so installed desktop apps pick this up automatically. The lockstep bump to `apps/desktop/package.json` alone does not require a desktop release.
- **Docs:** `Overall_Synopsis_of_Platform.md` updated (one line); `Data_Schema.md`, the README Features table, `docs/features.md` and the About-box `CAPABILITIES` unchanged - this is a layout change, not a scope change.
- The two screenshots from Task 3 Step 10.

---

## Open Decisions

Two calls made in this plan that the design handoff left ambiguous. Both are cheap to reverse if the answer is different:

1. **Tab ARIA.** The handoff says `role="tablist"` on the container but also "keep `aria-pressed`/`aria-selected`". A `role="tab"` element must not carry `aria-pressed`, so this plan adopts `role="tab"` + `aria-selected` (exactly what `DetailTabs` does) and updates seven test queries in `RecordingsPanel.test.tsx`. The alternative - plain buttons with `aria-pressed` inside a `role="tablist"` - keeps those queries untouched but is invalid ARIA and diverges from the detail tabs the design explicitly asks it to match.
2. **How the account menu reaches the room row.** The handoff describes both controls in one row. This plan gives `RoomSwitcher` a `leading` slot and has `Workspace` pass `<UserMenu />`, rather than having `RoomSwitcher` import `UserMenu` directly - the latter would pull react-query, `useAuth` and `useTour` into `RoomSwitcher.test.tsx`, which currently mocks `react-router-dom` down to `useNavigate` alone.
