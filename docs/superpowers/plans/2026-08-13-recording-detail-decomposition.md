# RecordingDetail.tsx Decomposition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring `apps/web/src/pages/RecordingDetail.tsx` down from 2,360 lines by moving out what is
already separable, without changing any behaviour.

**Architecture:** Three phases of increasing risk, deliberately ordered so the cheap certain wins land
first and the judgement call comes after real numbers are in. Phase 1 moves code that is already
prop-driven and needs no design thought. Phase 2 collapses nine identical async-action booleans into
one hook. Phase 3 is genuinely optional and gated on what Phases 1-2 actually deliver.

**Tech Stack:** React 19 + TypeScript, Vitest + @testing-library/react.

## Why this file, and what it is not

`RecordingDetail.tsx` has **never been refactored**. Across 108 commits only 13 ever reduced it, by
200 lines in total, and the largest single reduction is -55 in a feature commit. It grew 128 -> 2,360
essentially monotonically. There is no regression here to undo - this is a first decomposition.

The comparable precedent is `RecordingsPanel.tsx`, taken from a peak of **1,418 lines to 420** by PRs
#461 and #466-#470 (extract leaf components to `nav/`, give the Calendar and Tags tabs their own
state, move drop/paste and pure predicates to `lib/`). It has stayed at 420 since. That is the shape
being repeated here.

## Global Constraints

- **The 110 existing tests are the safety net**, across **two** files: `RecordingDetail.test.tsx` (84)
  and `RecordingDetail.speakers.test.tsx` (26). They must pass unchanged after every task. A task that
  needs them edited is a task that changed behaviour - stop and re-read the plan. **The one legitimate
  exception** is repointing an `import` at a symbol this plan deliberately moved: that is an address
  change, not a behaviour change. `RecordingDetail.speakers.test.tsx` imports `SpeakerRow` from the
  page and needs exactly that edit in Task 1.
- **No behaviour changes.** Not a prop default, not a class name, not an aria-label. If something
  looks wrong while moving it, leave it wrong and note it; fixing it here makes the diff unreviewable.
- **TDD applies to the new hooks** (Tasks 3+): failing test first, then the minimal code. The
  component moves in Tasks 1-2 are covered by the existing suite instead, which is the correct net for
  a pure move - see each task for how that is verified rather than assumed.
- **No em or en dashes in user-facing text.** Plain hyphen `-` in UI strings and i18n catalogues.
- **Never run `git add -A` in this repository.** Stage explicit paths.
- **Run the typecheck, not just the tests.** `npm run build` catches what `npx vitest` does not - it
  caught a real type error in PR #522 that all 2,400 tests missed.
- **Version bump:** each PR is a refactor, so **Build +1**. See Task 6.

## Phase plan and honest expectations

| Phase | Task | Expected reduction | Risk |
|---|---|---|---|
| 1 | Move 6 prop-driven components | ~478 lines | very low - they already take props |
| 1 | Move the icon block | ~35 lines | very low - module constants |
| 2 | `useAsyncAction` for 9 busy flags | ~70 lines | low - one uniform pattern |
| 3 | Transcript playback state | ~120 lines | **medium - real design** |
| 3 | Formula panel state | ~60 lines | medium |

Phases 1-2 land it around **1,780 lines**. That is a real improvement and it will not get the file
under 1,000. Anyone expecting a small file at the end should read that number now rather than later.

**Phase 3 is gated.** Stop after Task 4, measure, and decide. Do not start Task 5 or 6 on momentum.

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `apps/web/src/components/detail/RetranscribeModal.tsx` | Re-transcribe dialog (speaker-count bounds, revision warning) |
| `apps/web/src/components/detail/SegmentEditModal.tsx` | Edit one transcript segment's text |
| `apps/web/src/components/detail/RecordingNameForm.tsx` | Inline rename form in the header |
| `apps/web/src/components/detail/SegmentRow.tsx` | One transcript row, plus `NoteRow` beside it |
| `apps/web/src/components/detail/icons.tsx` | The Feather-style icon constants the toolbars share |
| `apps/web/src/lib/useAsyncAction.ts` | One in-flight async action: busy flag + error capture |
| `apps/web/src/lib/useAsyncAction.test.tsx` | Its tests |

**Modified**

- `apps/web/src/pages/RecordingDetail.tsx` throughout.

`components/detail/` already exists with 12 files and is where this page's components live, so these
join an established home rather than inventing one.

---

### Task 1: Move the six prop-driven components out

These are **module-level functions with explicit typed props**, declared after the main export. They
close over nothing from the page, so moving them is mechanical: cut, add `export default`, import.

**Two corrections found while executing this, kept here so the record matches what happened:**

1. There are **six**, not five. `SpeakerRow` (149 lines, line 2111) is declared `export function`, so
   a boundary scan matching only `^function ` misses it and silently swallows its body into
   `RecordingNameForm`. Match `^(export )?function [A-Z]`.
2. **`icons.tsx` (Task 2) has to land in the same commit**, because `SpeakerRow` renders
   `PencilIcon`, `PlayIcon`, `PauseIcon` and `TrashIcon`. Extracting it while the icons still live in
   the page would mean importing them back out of the page - a dependency pointing the wrong way.

Verified signatures, so the implementer does not have to derive them:

```
RetranscribeModal  { initialMin: number|null; initialMax: number|null; hasRevisions: boolean;
                     busy: boolean; onCancel: () => void;
                     onConfirm: (min: number|null, max: number|null) => void }
SegmentEditModal   { seg: SegmentDto; onClose: () => void; onSave: (text: string|null) => Promise<void> }
RecordingNameForm  { initial: string; onSave: (name: string) => void; onCancel: () => void }
SegmentRow         { seg: SegmentDto; speakerName: string; assign?: SegmentAssign; active: boolean;
                     selected: boolean; selectMode: boolean; showOriginal: boolean; onClick: ... }
NoteRow            { note: MeetingNote; speaker: string }
```

**Files:**
- Create: `apps/web/src/components/detail/RetranscribeModal.tsx` (from lines 1884-1973)
- Create: `apps/web/src/components/detail/SegmentEditModal.tsx` (from lines 1974-2075)
- Create: `apps/web/src/components/detail/RecordingNameForm.tsx` (from lines 2076-2259)
- Create: `apps/web/src/components/detail/SegmentRow.tsx` (from lines 2260-2361, both `SegmentRow` and `NoteRow`)
- Modify: `apps/web/src/pages/RecordingDetail.tsx`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: four default exports plus a named `NoteRow` from `SegmentRow.tsx`.

- [ ] **Step 1: Record the starting line count**

```bash
wc -l apps/web/src/pages/RecordingDetail.tsx
```

Expected: 2360. Write it down; Step 7 checks the arithmetic.

- [ ] **Step 2: Confirm the existing suite is green before touching anything**

```bash
cd apps/web && npx vitest run src/pages/RecordingDetail.test.tsx
```

Expected: 84 passed. A refactor that starts from red tells you nothing.

- [ ] **Step 3: Move `RetranscribeModal`**

Cut lines 1884-1973 into `apps/web/src/components/detail/RetranscribeModal.tsx`. Change
`function RetranscribeModal({` to `export default function RetranscribeModal({`. Add the imports it
needs at the top of the new file - determine them from what the body references, typically:

```tsx
import { useState } from "react";
import { useTranslation } from "react-i18next";
```

In `RecordingDetail.tsx` add:

```tsx
import RetranscribeModal from "../components/detail/RetranscribeModal";
```

- [ ] **Step 4: Move `SegmentEditModal`, `RecordingNameForm`, and `SegmentRow` + `NoteRow`**

Same treatment. `SegmentRow.tsx` holds both `SegmentRow` (default export) and `NoteRow`:

```tsx
export default function SegmentRow({ ... }) { ... }

/// A user's own note, rendered inline in the transcript at the moment it was written.
export function NoteRow({ note, speaker }: { note: MeetingNote; speaker: string }) { ... }
```

and in `RecordingDetail.tsx`:

```tsx
import SegmentRow, { NoteRow } from "../components/detail/SegmentRow";
```

`SegmentRow` references the `SegmentAssign` type and `segmentText` from `../../lib/transcriptView` -
import those in the new file rather than re-declaring them.

- [ ] **Step 5: Typecheck**

```bash
cd apps/web && npm run build
```

Expected: no `error TS` lines. Unused imports left behind in `RecordingDetail.tsx` show up here as
TS6133/TS6196 - delete them.

- [ ] **Step 6: Run the full suite**

```bash
cd apps/web && npx vitest run
```

Expected: all green, **with `RecordingDetail.test.tsx` still at 84 passed and unedited**. If a test
needed changing, something moved that should not have.

- [ ] **Step 7: Verify the move was a move, not a rewrite**

```bash
git diff --stat
```

Insertions into the four new files should closely match deletions from `RecordingDetail.tsx` (allow a
few lines for imports and the `export` keywords). A large net gain means code was rewritten rather
than moved - re-read the diff before continuing.

```bash
wc -l apps/web/src/pages/RecordingDetail.tsx
```

Expected: roughly 1,885.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/components/detail/RetranscribeModal.tsx apps/web/src/components/detail/SegmentEditModal.tsx apps/web/src/components/detail/RecordingNameForm.tsx apps/web/src/components/detail/SegmentRow.tsx apps/web/src/pages/RecordingDetail.tsx
git commit -m "refactor(detail): move the page's four prop-driven components to components/detail"
```

---

### Task 2: Move the icon constants

Lines **72-93** hold 12 Feather-style icon constants used by the page's toolbars.

`iconProps` is **not** defined in this file - it is imported from `../components/ToolbarButton`
(line 56). The new module imports it the same way; do not redeclare it.

**Files:**
- Create: `apps/web/src/components/detail/icons.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: named exports `RefreshIcon`, `PencilIcon`, `MailIcon`, `UsersIcon`, `SlidersIcon`,
  `PlayIcon`, `PauseIcon`, `SelectIcon`, `MergeIcon`, `TrashIcon`, `GlobeIcon`, `EyeIcon` - each a
  `ReactElement`, not a component. They are used as `{RefreshIcon}`, never `<RefreshIcon />`.

- [ ] **Step 1: Create the icons module**

Move every `const XxxIcon = ...` (lines 73-93) into `apps/web/src/components/detail/icons.tsx`,
exporting each:

```tsx
import { iconProps } from "../ToolbarButton";

// Feather-style icons for the recording page's panel toolbars and the per-speaker play control.
// These are ReactElements, not components - used as {RefreshIcon}, never <RefreshIcon />.

export const RefreshIcon = (
  <svg {...iconProps}><polyline points="23 4 23 10 17 10" />{/* ...verbatim... */}</svg>
);
// ...and the rest, verbatim.
```

`RecordingDetail.tsx` may still need its own `iconProps` import after this - the typecheck in Step 3
will say so either way.

Copy each icon's markup **exactly**. A redrawn path is a visual change, and no test will catch it.

- [ ] **Step 2: Import them back**

```tsx
import {
  RefreshIcon, PencilIcon, MailIcon, UsersIcon, SlidersIcon, PlayIcon,
  PauseIcon, SelectIcon, MergeIcon, TrashIcon, GlobeIcon, EyeIcon,
} from "../components/detail/icons";
```

Drop any that `RecordingDetail.tsx` does not actually use - the typecheck in Step 3 will name them.

- [ ] **Step 3: Typecheck and test**

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: no `error TS`, all tests green.

- [ ] **Step 4: Commit**

```bash
git add apps/web/src/components/detail/icons.tsx apps/web/src/pages/RecordingDetail.tsx
git commit -m "refactor(detail): move the recording page's icon constants into their own module"
```

---

### Task 3: Collapse the nine async-action booleans

Nine `useState` booleans - `requeuing`, `summarizing`, `extracting`, `reidentifying`, `renaming`,
`moving`, `sharing`, `downloading`, `translating` - all mean "this async action is in flight", and each
sits inside an identical block:

```tsx
setActionError(null);
setSummarizing(true);
try {
  await api.summarize(id);
  await qc.invalidateQueries({ queryKey: ["recording", id] });
} catch (e) {
  setActionError(apiErrorMessage(e, t("workspace:errSummarise")));
} finally {
  setSummarizing(false);
}
```

One hook replaces the flag and the ceremony around it.

**Files:**
- Create: `apps/web/src/lib/useAsyncAction.ts`
- Create: `apps/web/src/lib/useAsyncAction.test.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  `useAsyncAction(onError: (message: string) => void): { busy: (key: string) => boolean; run: (key: string, fn: () => Promise<void>, errorMessage: string) => Promise<void> }`

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/useAsyncAction.test.tsx`:

```tsx
import { render, act, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { useAsyncAction } from "./useAsyncAction";

vi.mock("./api", () => ({ apiErrorMessage: (_e: unknown, fb: string) => fb }));

let api: ReturnType<typeof useAsyncAction>;
const onError = vi.fn();

function Harness() {
  api = useAsyncAction(onError);
  return <span data-testid="s">{api.busy("summarise") ? "busy" : "idle"}</span>;
}
const state = () => document.querySelector('[data-testid="s"]')?.textContent;

beforeEach(() => vi.clearAllMocks());

describe("useAsyncAction", () => {
  it("is idle until something runs", () => {
    render(<Harness />);
    expect(state()).toBe("idle");
  });

  it("reports busy for the running key only, and clears when it settles", async () => {
    render(<Harness />);
    let release: () => void = () => {};
    const pending = new Promise<void>((r) => { release = r; });

    act(() => { void api.run("summarise", () => pending, "failed"); });
    expect(state()).toBe("busy");
    expect(api.busy("extract")).toBe(false); // a different action is unaffected

    await act(async () => { release(); await pending; });
    await waitFor(() => expect(state()).toBe("idle"));
  });

  it("reports the failure and still clears busy", async () => {
    render(<Harness />);

    await act(async () => {
      await api.run("summarise", () => Promise.reject(new Error("boom")), "Could not summarise");
    });

    expect(onError).toHaveBeenCalledWith("Could not summarise");
    await waitFor(() => expect(state()).toBe("idle"));
  });

  it("clears a previous error when a new action starts", async () => {
    render(<Harness />);
    await act(async () => {
      await api.run("summarise", () => Promise.reject(new Error("x")), "first failure");
    });
    onError.mockClear();

    await act(async () => { await api.run("summarise", () => Promise.resolve(), "unused"); });

    expect(onError).toHaveBeenCalledWith(null);
  });
});
```

- [ ] **Step 2: Run it and watch it fail**

```bash
cd apps/web && npx vitest run src/lib/useAsyncAction.test.tsx
```

Expected: FAIL - cannot resolve `./useAsyncAction`.

- [ ] **Step 3: Write the hook**

Create `apps/web/src/lib/useAsyncAction.ts`:

```ts
import { useState } from "react";
import { apiErrorMessage } from "./api";

/**
 * One in-flight async action at a time, keyed by name.
 *
 * The recording page ran nine of these by hand - a boolean per action, each wrapped in the same
 * clear-error / set-busy / try / catch / finally ceremony. Nine copies of one shape is nine chances
 * to forget the `finally` and strand a button in its busy state.
 *
 * `onError` receives the message to show, or null when a new action starts and the previous failure
 * should be cleared. The caller owns where that message is rendered.
 */
export function useAsyncAction(onError: (message: string | null) => void): {
  busy: (key: string) => boolean;
  run: (key: string, fn: () => Promise<void>, errorMessage: string) => Promise<void>;
} {
  const [running, setRunning] = useState<Record<string, boolean>>({});

  return {
    busy: (key) => running[key] === true,

    async run(key, fn, errorMessage) {
      onError(null);
      setRunning((r) => ({ ...r, [key]: true }));
      try {
        await fn();
      } catch (e) {
        onError(apiErrorMessage(e, errorMessage));
      } finally {
        // Always, even on the failure path: a stranded busy flag leaves a button dead for the rest
        // of the session with nothing on screen to explain it.
        setRunning((r) => ({ ...r, [key]: false }));
      }
    },
  };
}
```

- [ ] **Step 4: Run the tests**

```bash
cd apps/web && npx vitest run src/lib/useAsyncAction.test.tsx
```

Expected: PASS (4 tests).

- [ ] **Step 5: Mutation-check the `finally`**

The whole point of the hook is that busy always clears. Temporarily move the `setRunning(false)` line
out of `finally` and into the `try` block after `await fn()`, then re-run. **"reports the failure and
still clears busy" must FAIL.** Restore it and confirm green. A test that cannot catch a stranded busy
flag is not testing the thing this hook exists for.

- [ ] **Step 6: Convert the nine call sites**

In `RecordingDetail.tsx`, add:

```tsx
const action = useAsyncAction(setActionError);
```

Delete the nine `useState` declarations (lines 347-354 and 367). Then rewrite each action. The
summarise one becomes:

```tsx
async function summarise() {
  if (rec?.summary?.isUserEdited && !window.confirm(t("workspace:confirmResummarise"))) return;
  await action.run("summarise", async () => {
    await api.summarize(id);
    await qc.invalidateQueries({ queryKey: ["recording", id] });
  }, t("workspace:errSummarise"));
}
```

Note the confirm stays **outside** `run` - it is a precondition, and running it inside would clear a
visible error before the user has decided anything.

Every read of a flag becomes a `busy` call: `disabled={summarizing}` becomes
`disabled={action.busy("summarise")}`. Keep the key strings consistent between the `run` and the
`busy` for each action - a typo silently leaves a button permanently idle, and no test will catch it,
so **grep each key after converting**:

```bash
grep -o 'action\.\(run\|busy\)("[a-z]*"' apps/web/src/pages/RecordingDetail.tsx | sort | uniq -c
```

Every key must appear at least twice - once for the `run`, once or more for the `busy`.

- [ ] **Step 7: Typecheck and run everything**

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: no `error TS`; all green with `RecordingDetail.test.tsx` unedited at 84 passed.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/useAsyncAction.ts apps/web/src/lib/useAsyncAction.test.tsx apps/web/src/pages/RecordingDetail.tsx
git commit -m "refactor(detail): collapse nine async-action booleans into useAsyncAction"
```

---

### Task 4: Measure, and decide whether Phase 3 happens

Not a code task. This is the gate, and skipping it is how a refactor turns into a rewrite nobody
asked for.

- [ ] **Step 1: Measure**

```bash
wc -l apps/web/src/pages/RecordingDetail.tsx
grep -c "useState(" apps/web/src/pages/RecordingDetail.tsx
```

Expected: roughly 1,780 lines and 22 `useState`.

- [ ] **Step 2: Compare against the prediction and report it**

The table at the top of this plan predicted ~1,780. Report the real number to the user **including if
it is worse than predicted**, alongside the useLiveNotes precedent: that refactor removed 34 lines for
a whole PR, and its value turned out to be the tests rather than the size.

- [ ] **Step 3: Decide with the user, do not decide alone**

Phase 3 (Tasks 5-6) touches the transcript playback and formula state, which is live interactive
behaviour with thinner test coverage than the rest of the page. Ask whether to proceed. **Stop here if
the answer is no** - Phases 1-2 are a complete, shippable change on their own, and Task 7's release
steps apply either way.

---

### Task 5: Transcript playback and selection state (Phase 3 - gated)

**Only start this if Task 4's decision said yes.**

Eight pieces of state form the transcript player: `activeIdx`, `playingSpeaker`, `selectedSpeaker`,
`selectMode`, `selectedSegIds`, `audioCur`, `audioPaused`, `selectionPlaying` (lines 256-287). They
move together and are read together.

**Files:**
- Create: `apps/web/src/lib/useTranscriptPlayback.ts`
- Create: `apps/web/src/lib/useTranscriptPlayback.test.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx`

**Interfaces:**
- Consumes: `PlayRange` from `lib/segmentPlayback` (line 65 of the page - alongside `speakerRanges`,
  `selectedRanges`, `rangeAt` and `nextRangeStart`, which this state is coupled to), `SegmentDto` from
  `lib/types`.
- Produces: `useTranscriptPlayback({ segments }: { segments: SegmentDto[] })` returning
  `{ activeIdx: number; audioCur: number; audioPaused: boolean; selectMode: boolean;
     selectedSegIds: Set<string>; playingSpeaker: string | null; selectedSpeaker: string | null;
     selectionPlaying: boolean; setPosition(ms: number): void; toggleSelect(id: string): void;
     clearSelection(): void; enterSelectMode(): void; exitSelectMode(): void }`

- [ ] **Step 1: Read every use before moving anything**

```bash
grep -n "activeIdx\|playingSpeaker\|selectedSpeaker\|selectMode\|selectedSegIds\|audioCur\|audioPaused\|selectionPlaying" apps/web/src/pages/RecordingDetail.tsx
```

This state is coupled to an `<audio>` element and to `segmentIndexAtMs`. **Write down every read and
write before extracting** - unlike Tasks 1-3, the boundary here is a judgement call, not a given, and
the wrong one shows up as a subtly broken player that the tests may not catch.

- [ ] **Step 2: Write the failing tests**

Create `apps/web/src/lib/useTranscriptPlayback.test.tsx`:

```tsx
import { render, act } from "@testing-library/react";
import { describe, it, expect, beforeEach } from "vitest";
import { useTranscriptPlayback } from "./useTranscriptPlayback";
import type { SegmentDto } from "./types";

const seg = (id: string, startMs: number, endMs: number): SegmentDto =>
  ({ id, startMs, endMs, text: id, speaker: "SPEAKER_00" } as SegmentDto);

const SEGMENTS = [seg("a", 0, 1_000), seg("b", 1_000, 2_000), seg("c", 2_000, 3_000)];

let api: ReturnType<typeof useTranscriptPlayback>;
function Harness() {
  api = useTranscriptPlayback({ segments: SEGMENTS });
  return <span data-testid="i">{api.activeIdx}</span>;
}
const idx = () => document.querySelector('[data-testid="i"]')?.textContent;

beforeEach(() => { /* fresh render per test */ });

describe("useTranscriptPlayback", () => {
  it("starts before any segment is active", () => {
    render(<Harness />);
    expect(idx()).toBe("-1");
  });

  it("tracks which segment the playhead is inside", () => {
    render(<Harness />);
    act(() => api.setPosition(1_500));
    expect(idx()).toBe("1");
  });

  it("selects and deselects individual segments", () => {
    render(<Harness />);
    act(() => api.toggleSelect("b"));
    expect([...api.selectedSegIds]).toEqual(["b"]);
    act(() => api.toggleSelect("b"));
    expect([...api.selectedSegIds]).toEqual([]);
  });

  it("leaving select mode drops the selection, so it cannot act on a hidden set", () => {
    render(<Harness />);
    act(() => { api.enterSelectMode(); api.toggleSelect("a"); });
    act(() => api.exitSelectMode());
    expect(api.selectMode).toBe(false);
    expect([...api.selectedSegIds]).toEqual([]);
  });
});
```

- [ ] **Step 3: Run and watch them fail**

```bash
cd apps/web && npx vitest run src/lib/useTranscriptPlayback.test.tsx
```

Expected: FAIL - cannot resolve `./useTranscriptPlayback`.

- [ ] **Step 4: Write the hook by moving the existing logic**

Move the eight `useState` declarations and the functions that write them out of `RecordingDetail.tsx`
into the new hook, preserving the current behaviour exactly. `activeIdx` derives from `audioCur` via
`segmentIndexAtMs(segments, ms)` - keep that call, do not reimplement the search.

- [ ] **Step 5: Tests pass, then wire the page up**

```bash
cd apps/web && npx vitest run src/lib/useTranscriptPlayback.test.tsx
```

Expected: PASS (4 tests). Then replace the page's state with `const playback = useTranscriptPlayback({ segments })`
and update every reader.

- [ ] **Step 6: Typecheck and full suite**

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: all green, `RecordingDetail.test.tsx` unedited.

- [ ] **Step 7: Verify the player in a browser, because jsdom cannot**

Playback position, highlight-follows-audio and click-to-seek are timing and geometry behaviours that
jsdom does not model. Start the dev server, open a transcribed recording, and confirm: play advances
the highlight; clicking a segment seeks; entering select mode and picking rows still works; leaving
select mode clears the selection.

```bash
cd apps/web && npm run dev
```

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/useTranscriptPlayback.ts apps/web/src/lib/useTranscriptPlayback.test.tsx apps/web/src/pages/RecordingDetail.tsx
git commit -m "refactor(detail): move transcript playback and selection state into a hook"
```

---

### Task 6: Formula panel state (Phase 3 - gated)

**Only start this if Task 4's decision said yes and Task 5 landed cleanly.**

Five pieces of state drive the formula panels: `selectedFormulaResultId`, `formulaRunOpen`,
`editingFormulaResult`, `managingFormulas`, `sharedBrowserOpen` (lines 270-276).

**Files:**
- Create: `apps/web/src/lib/useFormulaPanels.ts`
- Create: `apps/web/src/lib/useFormulaPanels.test.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx`

**Interfaces:**
- Consumes: `FormulaResult` from `lib/types`.
- Produces: `useFormulaPanels()` returning
  `{ selectedResultId: string | null; runOpen: boolean; editing: FormulaResult | null;
     managing: boolean; browsingShared: boolean; select(id: string | null): void;
     openRun(): void; closeRun(): void; edit(r: FormulaResult | null): void;
     setManaging(on: boolean): void; setBrowsingShared(on: boolean): void }`

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/useFormulaPanels.test.tsx`:

```tsx
import { render, act } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { useFormulaPanels } from "./useFormulaPanels";
import type { FormulaResult } from "./types";

const result = { id: "r1", formulaId: "f1", markdown: "# doc" } as FormulaResult;

let api: ReturnType<typeof useFormulaPanels>;
function Harness() {
  api = useFormulaPanels();
  return <span data-testid="s">{api.runOpen ? "open" : "closed"}</span>;
}
const state = () => document.querySelector('[data-testid="s"]')?.textContent;

describe("useFormulaPanels", () => {
  it("starts with every panel closed and nothing selected", () => {
    render(<Harness />);
    expect(state()).toBe("closed");
    expect(api.selectedResultId).toBeNull();
    expect(api.editing).toBeNull();
  });

  it("opens and closes the run panel", () => {
    render(<Harness />);
    act(() => api.openRun());
    expect(state()).toBe("open");
    act(() => api.closeRun());
    expect(state()).toBe("closed");
  });

  it("holds the result being edited, and lets it go", () => {
    render(<Harness />);
    act(() => api.edit(result));
    expect(api.editing).toEqual(result);
    act(() => api.edit(null));
    expect(api.editing).toBeNull();
  });

  it("selecting a result does not open the run panel", () => {
    render(<Harness />);
    act(() => api.select("r1"));
    expect(api.selectedResultId).toBe("r1");
    expect(state()).toBe("closed");
  });
});
```

- [ ] **Step 2: Run and watch it fail**

```bash
cd apps/web && npx vitest run src/lib/useFormulaPanels.test.tsx
```

Expected: FAIL - cannot resolve `./useFormulaPanels`.

- [ ] **Step 3: Write the hook**

Move the five `useState` declarations into `apps/web/src/lib/useFormulaPanels.ts`, exposing the
interface above. This is plain state grouping - no derived values, no effects.

- [ ] **Step 4: Tests pass, then wire the page up**

```bash
cd apps/web && npx vitest run src/lib/useFormulaPanels.test.tsx
```

Expected: PASS (4 tests). Then replace the page's five declarations with
`const formulas = useFormulaPanels()` and update every reader.

- [ ] **Step 5: Typecheck and full suite**

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: all green, `RecordingDetail.test.tsx` unedited.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/lib/useFormulaPanels.ts apps/web/src/lib/useFormulaPanels.test.tsx apps/web/src/pages/RecordingDetail.tsx
git commit -m "refactor(detail): move formula panel state into a hook"
```

---

### Task 7: Release

Applies whether the work stopped after Task 4 or ran to Task 6.

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Bump the version**

A refactor is **Build +1**. From `0.211.1` that is **`0.211.2`** - confirm the current value first,
since other PRs may have landed:

```bash
cat version.json
```

Change it in all five files above.

- [ ] **Step 2: Verify the mirrors**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

`versionMirrors` must pass. `releases` will FAIL until Step 3 - that is the assertion doing its job.

- [ ] **Step 3: Add the release entry**

At the top of `RELEASES` in `apps/web/src/lib/releases.ts`, with `pr: 0` for now:

```ts
  {
    version: "0.211.2",
    date: "<today>",
    pr: 0,
    headline: "Internal tidy-up of the recording page, with no change to what it does",
    summary:
      "Housekeeping only - nothing you can see changes. The recording page had grown to be the " +
      "largest file in the app, so the dialogs and transcript rows it draws have been moved into " +
      "files of their own, and the nine separate 'this is running' flags behind buttons like " +
      "Summarise and Re-transcribe are now handled in one place. Everything on the page behaves " +
      "exactly as before.",
    changed: [
      "Internal: the recording page's dialogs, transcript rows and async-action handling moved into separate modules.",
    ],
  },
```

No scope change, so `CAPABILITIES`, the README feature table, `docs/features.md`,
`Overall_Synopsis_of_Platform.md` and `Data_Schema.md` are all **untouched** - nothing they describe
has moved.

- [ ] **Step 4: Verify everything before asking for review**

```bash
cd apps/web && npm run build && npx vitest run
cd ../.. && dotnet build Diariz.slnx
```

- [ ] **Step 5: Push, open the PR, then correct the number**

The `pr:` field needs a number that does not exist until the PR does, and guessing "last + 1" fails -
Dependabot and issues share the sequence.

```bash
git push -u origin refactor/recording-detail
gh pr create --title "Decompose RecordingDetail.tsx" --body "..."
```

State in the body: **server redeploy only, no desktop release** - no shell files are touched, so the
`apps/desktop/package.json` bump is lockstep versioning only. Report the real before/after line count,
including if it undershot the prediction.

- [ ] **Step 6: Commit the PR number**

```bash
git add apps/web/src/lib/releases.ts
git commit -m "docs: record the PR number in the release entry"
git push
```

---

## Notes for the implementer

**The existing 84 tests are the whole safety net for Tasks 1-2.** Those are pure moves, so no new test
is written for them - the correct verification is that the suite passes *unedited*, plus the
diff-stat check in Task 1 Step 7 confirming lines moved rather than got rewritten. If you find
yourself editing `RecordingDetail.test.tsx`, stop: you have changed behaviour.

**Tasks 3, 5 and 6 are TDD proper** - failing test first, then the hook.

**Do not fix things you notice on the way.** This file is 2,360 lines and has grown over 108 commits;
there will be things worth improving. Note them, leave them. A refactor diff that also contains fixes
cannot be reviewed as either one.

**Expect the numbers to disappoint slightly.** The comparable useLiveNotes extraction moved 34 lines
and its real value was the tests it forced. Phases 1-2 here should move roughly 580, which is a better
ratio - but "still 1,780 lines" is the honest outcome, not "problem solved".
