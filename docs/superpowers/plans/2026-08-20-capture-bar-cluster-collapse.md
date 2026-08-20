# Capture bar cluster collapse - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The record cluster keeps shedding chrome below the existing `@xl` label step, and past that folds its secondary controls into a `...` menu, so nothing in the capture bar becomes unreachable in a narrow window.

**Architecture:** Two more container-query tiers on the bar's existing `@container`. Tier C ("tight") sheds chrome; tier D ("cramped") swaps the secondary icon buttons for a `HubIconButton` that opens a new `"more"` hub popover. The menu never nests popovers - `HubPopover` panels drop from the bar, not from their trigger, so a menu row just calls `hub.toggle("stop" | "notes")` and the hub's one-open-at-a-time rule closes the menu as the real popover opens. Thresholds differ by recording state and are written as literal class strings (Tailwind cannot see a concatenated variant).

**Tech Stack:** React 19 + TypeScript + Tailwind v4, vitest + @testing-library/react, i18next.

**Spec:** `docs/superpowers/specs/2026-08-20-capture-bar-cluster-collapse-design.md`

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail with a real message, then write the minimal code to pass.
- **Test output must be pristine.** A passing run has no errors or warnings.
- **No em/en dashes in user-facing text.** Plain hyphen `-` only. Applies to UI strings, all four i18n catalogs, and release notes.
- **Never commit to `main`.** Work lands on branch `feat/capture-bar-cluster-collapse` and merges via a PR.
- **Never `git add -A` in this repository.** Stage explicit paths only.
- **Do not add a `jest-dom` dependency.** Use plain assertions.
- **`--filter "Name=X"` does not work here.** Use `FullyQualifiedName~X` for .NET; this plan is web-only so it is vitest throughout.
- This is a **web-only** change. Do not touch `src/`, `tests/`, or `apps/desktop/`.
- Version target: `0.233.1` -> **`0.234.0`**.

### Verified before planning

- Tailwind v4.3.3 emits arbitrary max container variants: `@max-[440px]:hidden` compiles to `@container not (width>=440px)`. Confirmed by building with a probe file and grepping `dist/assets/index-*.css`.
- `HubIconButton` sets `display: flex` as an **inline style**, so a `hidden` class on the button itself does nothing. Every tier class in this plan goes on a wrapper element.

---

### Task 1: The `more` popover id

**Files:**
- Modify: `apps/web/src/components/hub/hubPopovers.tsx`

**Interfaces:**
- Produces: `HubPopoverId` gains `"more"`. Consumed by Task 3.

- [ ] **Step 1:** Add `"more"` to the `HubPopoverId` union and to the doc comment listing the popovers. No test - it is a type-only change with no behaviour, and Task 3's tests exercise it.

---

### Task 2: Tier C - shed chrome

**Files:**
- Modify: `apps/web/src/components/hub/AudioSourceChip.tsx`
- Modify: `apps/web/src/components/hub/RecordHero.tsx`
- Modify: `apps/web/src/components/hub/CaptureBar.tsx`
- Test: `apps/web/src/components/hub/AudioSourceChip.test.tsx`, `RecordHero.test.tsx`, `CaptureBar.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: `AudioSourceChip` and `RecordHero` gain no new props - both already know whether they are recording, and the chip's threshold is the idle one because the chip is identical in both states. `RecordHero` picks its threshold from its own `recording` prop.

- [ ] **Step 1: Write the failing tests.** Class-contract assertions only - state in each test's comment that jsdom computes no geometry, so these prove the classes are present and nothing about layout.
  - `AudioSourceChip`: with `systemAudio` on, the `+System` pill carries `@max-[400px]:hidden` and a sibling dot element carries `@max-[400px]:block` (hidden above it); the chevron's wrapper carries `@max-[400px]:hidden`.
  - `RecordHero` recording: the level meter's wrapper carries `@max-[690px]:hidden`.
  - `CaptureBar`: the bar carries the tightened padding/gap classes.
- [ ] **Step 2: Implement.**
  - `AudioSourceChip`: wrap the chevron in a `<span className="@max-[400px]:hidden">`. Replace the `+System` pill with a pair - the existing pill gaining `@max-[400px]:hidden`, and a new 8px green dot `<span className="hidden @max-[400px]:block">` using `--hub-green-text`. The dot needs no accessible text: the chip's `aria-label` already names it and the popover states the source.
  - `RecordHero`: wrap `<HubLevelMeter>` in `<div className="@max-[690px]:hidden">`. **Keep it mounted** - it is what detects silence via `onSilentChange`, and unmounting it would silently disable the "no sound" hint at narrow widths.
  - `CaptureBar`: move the inline `padding: "0 18px"` onto `className` as `px-[18px] @max-[480px]:px-2`, and change `gap-4` to `gap-4 @max-[480px]:gap-1`. One shared threshold here, not per-state: the bar does not know the recording state, and the 44px this saves is noise at the recording tier's 690px.
- [ ] **Step 3:** `npx vitest run src/components/hub` - green, no warnings.

---

### Task 3: Tier D - the overflow menu

**Files:**
- Create: `apps/web/src/components/hub/MoreControlsPopover.tsx`
- Modify: `apps/web/src/components/Recorder.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Test: `apps/web/src/components/hub/MoreControlsPopover.test.tsx`, `apps/web/src/components/Recorder.test.tsx`

**Interfaces:**
- Consumes: `"more"` from Task 1.
- Produces: `MoreControlsPopover`, a presentational list of rows. It takes `open`, `onClose`, and one optional handler per row; a row is absent when its handler is absent, which is how the idle/recording difference is expressed - no `recording` prop.

New i18n keys in all four catalogs: `moreControls` ("More controls"), and the rows reuse the existing `autoStopLabel`, `recUpload`, `screenshotCaptureButton`, `liveNotesToggle`.

- [ ] **Step 1: Write the failing tests for `MoreControlsPopover`.** Rendering with a subset of handlers shows exactly those rows; each row invokes its handler; a row with a `disabledReason` is inert and carries the reason as its title. Follow the `HubPopover`-based pattern in `AutoStopPopover`/`NotesPopover`.
- [ ] **Step 2: Implement `MoreControlsPopover`.** A `HubPopover` (`ariaLabel` = `moreControls`, `anchorClassName="right-0"`, width 240) wrapping a vertical list of buttons, each `[16px glyph] [label]`. Reuse the glyph components already in `Recorder.tsx` by lifting them if needed - do not draw second copies.
- [ ] **Step 3: Write the failing tests in `Recorder.test.tsx`.** Scope every query with `within(screen.getByRole("dialog", { name: /more controls/i }))` - the inline buttons also exist in jsdom, so an unscoped `getByRole("button", { name: /auto-stop/i })` matches two elements. Cover:
  - The `...` button is present and opens the menu.
  - Idle: rows are Auto-stop and Upload; no Notes, no Screenshot.
  - Choosing Auto-stop closes the menu and opens the auto-stop popover (assert the auto-stop dialog is present and the more dialog is gone) - this is the no-nesting claim and is the most important test here.
  - Choosing Upload closes the menu and opens the file dialog (the existing tests already have a handle on the hidden `data-testid="upload-input"`).
- [ ] **Step 4: Implement in `Recorder.tsx`.**
  - Wrap the auto-stop, upload, screenshot and notes **buttons** (not their `relative` wrappers) in tier-hiding elements. The auto-stop and notes wrappers must stay in flow: they host the popovers the menu opens, and a hidden wrapper hides the popover inside it.
  - Add the `...` trigger in its own `relative` wrapper, hidden above tier D, with `MoreControlsPopover` beside it.
  - Hide Upload inline while recording (tier C) - it is already `disabled` in that state - and omit its row from the menu while recording.
  - Threshold selection, written as literal strings so Tailwind generates both:
    ```tsx
    const hideWhenCramped = recording ? "@max-[440px]:hidden" : "@max-[240px]:hidden";
    const showWhenCramped = recording ? "hidden @max-[440px]:block" : "hidden @max-[240px]:block";
    ```
- [ ] **Step 5:** `npx vitest run src/components/Recorder.test.tsx src/components/hub` - green, no warnings.

---

### Task 4: Browser verification of the floors

**Files:** none committed. Scratchpad only.

The eight numbers in the spec's tier table are the actual claim of this change, and no jsdom test can check them.

- [ ] **Step 1:** Start the dev server (`.claude/launch.json` config `web`, port 5199).
- [ ] **Step 2:** Build a replica page under `apps/web/public/` that uses the **real class strings and the compiled Tailwind CSS** (`<link rel="stylesheet" href="/src/index.css">`), for both the idle and the recording cluster.
- [ ] **Step 3:** For each tier, narrow the container until the cluster overflows and record the width. Confirm each measured floor is at or below the spec's figure, and that each tier's threshold sits above the floor of the tier below it (no width at which the cluster overflows before its next tier engages).
- [ ] **Step 4:** Delete the replica page. It must not be committed.

---

### Task 5: Release

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `integrations/n8n-nodes-diariz/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1:** Bump `0.233.1` -> `0.234.0` in `version.json` and all four mirrors. `versionMirrors.test.ts` fails the build if any drifts.
- [ ] **Step 2:** Add the `RELEASES[0]` entry. Use `chr(92)` if building `\n` through a shell heredoc - a literal `\n` in a heredoc lands as a real newline and breaks the string.
- [ ] **Step 3:** No `CAPABILITIES` / README / `docs/features.md` edit: the bar offers the same controls and does the same things. No architecture or schema change. Say so in the PR.
- [ ] **Step 4:** `npm test` and `npm run build` in `apps/web` - both clean.
- [ ] **Step 5:** Push the branch and open a PR stating the deployment surface: **server redeploy only**, no desktop release.
