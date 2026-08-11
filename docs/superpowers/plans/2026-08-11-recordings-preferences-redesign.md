# Recordings Preferences Panel Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the Preferences -> Recordings panel as choice cards plus a single auto-stop switch with inline sentence controls, move its Save into the modal footer, and put the folder chooser in its own dialog - with no server, schema or payload change.

**Architecture:** Three pieces. (1) A new `PreferencesFooter.tsx` holding a registration context and the footer bar; a tab opts in and the shared footer paints its Save button and status line, while the other five tabs are untouched. (2) `RecordingsSection.tsx` is rebuilt as two card groups - three radio cards for placement, one bordered card with a `role="switch"` for calendar auto-stop - registering with that footer. (3) A new `FolderPickerModal.tsx` supplies dialog chrome around the existing `FolderPicker`, which is rendered unchanged.

**Tech Stack:** React 19, TypeScript, Tailwind v4, `react-i18next`, `@tanstack/react-query`, Vitest + `@testing-library/react` (jsdom).

**Spec:** `docs/superpowers/specs/2026-08-11-recordings-preferences-redesign-design.md`

## Global Constraints

- **TDD is mandatory.** Failing test first, watch it fail, then the minimal code. No production code without a test that preceded it.
- **The save payload must not change.** `api.updateUserSettings` receives exactly these five fields and nothing else: `placementMode`, `placementSectionId` (`null` for any mode other than `SpecificFolder`), `calendarAutoStopEnabled`, `calendarAutoStopAfterMinutes`, `calendarSilenceStopSeconds`.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only, in UI strings, all i18n catalogues, release notes and help articles. Never `—` or `–`.
- **The user-facing noun is "Folder", never "Section".** The handoff says Section; the repo owner decided otherwise. Code, DB and API keep `Section`.
- **i18n parity is gated.** `apps/web/src/locales.test.ts` fails the build if `de`/`es`/`fr` do not have exactly the same keys as `en`, or if any value is empty. Every key added or removed must be done in all four catalogues in the same commit.
- **Help content is ASCII only** and carries `title` / `summary` / `group` / `order` front matter. `apps/web/src/content/help/helpContent.test.ts` enforces this.
- **Never `git add -A` in this repo.** Stage explicit paths only - a bare `-A` sweeps hundreds of agent scratch files into the commit.
- **Branch:** `feat/recordings-preferences-redesign` (already created, spec already committed on it). `main` is protected: finish by pushing and opening a PR, never by merging locally.
- **Working directory for all commands:** `apps/web` unless stated otherwise.
- Run the whole web suite with `npm test`. Run one file with `npm test -- src/components/Foo.test.tsx`.

---

## File Structure

| File | Responsibility |
| :--- | :--- |
| Create `apps/web/src/components/PreferencesFooter.tsx` | The footer registration context, the tab-side `usePreferencesFooter` hook, and the `PreferencesFooterBar` component. Its own file so a test can mock a tab that registers without a circular import back into `PreferencesModal`. |
| Create `apps/web/src/components/PreferencesFooter.test.tsx` | Unit tests for the context in isolation. |
| Create `apps/web/src/components/FolderPickerModal.tsx` | Dialog chrome (header, footer, Escape, focus) around an unchanged `FolderPicker`. |
| Create `apps/web/src/components/FolderPickerModal.test.tsx` | Its tests. |
| Modify `apps/web/src/components/PreferencesModal.tsx` | Wrap the content in the provider, render `PreferencesFooterBar` instead of the bare Close button, add the breadcrumb. |
| Modify `apps/web/src/components/PreferencesModal.test.tsx` | Footer registration and breadcrumb tests. |
| Modify `apps/web/src/components/RecordingsSection.tsx` | The redesign. |
| Modify `apps/web/src/components/RecordingsSection.test.tsx` | Rewritten around the new UI; every payload assertion kept. |
| Modify `apps/web/src/locales/{en,de,es,fr}/account.json` | Panel copy. |
| Modify `apps/web/src/locales/{en,de,es,fr}/workspace.json` | Picker dialog chrome copy. |
| Modify `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json` | Version bump, lockstep. |
| Modify `apps/web/src/lib/releases.ts` | The release entry. |
| Modify `docs/features.md`, `apps/web/src/content/help/en/recording-audio.md`, `apps/web/src/content/help/en/organizing-folders.md` | Stale references to the panel's old labels. |

**Deliberately NOT modified:** `FolderPicker.tsx` (and therefore `MoveToSectionModal.tsx`), `icons.tsx` (the codebase's close-button convention is a literal `✕` character with an `aria-label` - see `ManageUsersModal.tsx:65`, `ManageMeetingTypesModal.tsx:208` - so no new icon component is introduced), anything under `src/`, `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`.

---

### Task 1: Footer registration context and bar

Builds the mechanism only. Nothing consumes it yet, so the other five tabs must be provably unaffected.

**Files:**
- Create: `apps/web/src/components/PreferencesFooter.tsx`
- Create: `apps/web/src/components/PreferencesFooter.test.tsx`
- Modify: `apps/web/src/components/PreferencesModal.tsx` (header block at lines 119-121, footer block at lines 130-138)
- Modify: `apps/web/src/components/PreferencesModal.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `type FooterSaveStatus = "idle" | "unsaved" | "saved"`
  - `interface FooterSaveState { dirty: boolean; busy: boolean; status: FooterSaveStatus; error: string | null }`
  - `function PreferencesFooterProvider({ children }: { children: ReactNode }): JSX.Element`
  - `function usePreferencesFooter(reg: FooterSaveState & { onSave: () => void }): void` - the tab-side hook
  - `function PreferencesFooterBar({ onClose }: { onClose: () => void }): JSX.Element`

- [ ] **Step 1: Write the failing context tests**

Create `apps/web/src/components/PreferencesFooter.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import { useState } from "react";
import { PreferencesFooterProvider, PreferencesFooterBar, usePreferencesFooter } from "./PreferencesFooter";

/// A stand-in for a tab that opts into the footer. `onSave` is a fresh closure on every render on
/// purpose - that is the exact shape that would loop a naive registration effect.
function RegisteringTab({ onSave, status = "unsaved", busy = false, error = null }: {
  onSave: () => void;
  status?: "idle" | "unsaved" | "saved";
  busy?: boolean;
  error?: string | null;
}) {
  usePreferencesFooter({ dirty: status === "unsaved", busy, status, error, onSave: () => onSave() });
  return <div>TAB</div>;
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <PreferencesFooterProvider>
      {children}
      <PreferencesFooterBar onClose={() => {}} />
    </PreferencesFooterProvider>
  );
}

describe("PreferencesFooter", () => {
  it("shows Close alone when no tab has registered", () => {
    render(<Shell><div>PLAIN_TAB</div></Shell>);
    expect(screen.getByRole("button", { name: /close/i })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();
  });

  it("shows Save changes and the status line once a tab registers", () => {
    render(<Shell><RegisteringTab onSave={() => {}} /></Shell>);
    expect(screen.getByRole("button", { name: /save changes/i })).toBeTruthy();
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
  });

  it("calls the registering tab's handler, using the latest closure rather than the first", () => {
    const first = vi.fn();
    const second = vi.fn();
    function Swapper() {
      const [fn, setFn] = useState(() => first);
      return (
        <>
          <button type="button" onClick={() => setFn(() => second)}>swap</button>
          <RegisteringTab onSave={fn} />
        </>
      );
    }
    render(<Shell><Swapper /></Shell>);
    fireEvent.click(screen.getByRole("button", { name: "swap" }));
    fireEvent.click(screen.getByRole("button", { name: /save changes/i }));
    expect(second).toHaveBeenCalled();
    expect(first).not.toHaveBeenCalled();
  });

  it("deregisters when the tab unmounts, restoring the plain footer", () => {
    function Toggle() {
      const [on, setOn] = useState(true);
      return (
        <>
          <button type="button" onClick={() => setOn(false)}>hide</button>
          {on && <RegisteringTab onSave={() => {}} />}
        </>
      );
    }
    render(<Shell><Toggle /></Shell>);
    expect(screen.getByRole("button", { name: /save changes/i })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "hide" }));
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();
  });

  it("renders Saved, and an error in place of the status", () => {
    const { rerender } = render(<Shell><RegisteringTab onSave={() => {}} status="saved" /></Shell>);
    expect(screen.getByText("Saved")).toBeTruthy();

    rerender(<Shell><RegisteringTab onSave={() => {}} status="idle" error="Could not save." /></Shell>);
    const err = screen.getByText("Could not save.");
    expect(err.className).toContain("text-red");
  });

  it("disables Save while busy", () => {
    render(<Shell><RegisteringTab onSave={() => {}} busy /></Shell>);
    expect((screen.getByRole("button", { name: /saving/i }) as HTMLButtonElement).disabled).toBe(true);
  });
});
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `npm test -- src/components/PreferencesFooter.test.tsx`
Expected: FAIL - `Failed to resolve import "./PreferencesFooter"`.

- [ ] **Step 3: Write `PreferencesFooter.tsx`**

```tsx
import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

export type FooterSaveStatus = "idle" | "unsaved" | "saved";

/// The primitives the footer paints from. `onSave` is deliberately NOT one of them: a tab's handler is a
/// fresh closure on every render, and putting it in the registration effect's dependency list would
/// re-register on every render forever. It is carried in a ref instead (see `usePreferencesFooter`).
export interface FooterSaveState {
  dirty: boolean;
  busy: boolean;
  status: FooterSaveStatus;
  error: string | null;
}

/// Split into two contexts on purpose. The api half is memoised once and never changes identity, so a tab
/// can depend on it in an effect; the state half changes on every registration. One combined context
/// would make the api value change whenever the state did, re-running the tab's effect, which would
/// register again and loop.
const FooterApiCtx = createContext<{
  register: (state: FooterSaveState | null) => void;
  saveRef: React.MutableRefObject<(() => void) | null>;
} | null>(null);
const FooterStateCtx = createContext<FooterSaveState | null>(null);

/// Holds whichever tab has opted into the modal footer. Exactly one tab is mounted at a time, so a single
/// slot is enough - there is no registry and no ordering to resolve.
export function PreferencesFooterProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<FooterSaveState | null>(null);
  const saveRef = useRef<(() => void) | null>(null);
  const api = useMemo(() => ({ register: setState, saveRef }), []);
  return (
    <FooterApiCtx.Provider value={api}>
      <FooterStateCtx.Provider value={state}>{children}</FooterStateCtx.Provider>
    </FooterApiCtx.Provider>
  );
}

/// Opt this tab into the shared footer: the modal paints its Save button and status line. A tab that never
/// calls this keeps its own in-body Save and sees the plain Close-only footer, which is the case for five
/// of the six tabs. Outside a provider this is a no-op, so a tab still renders standalone in a test.
export function usePreferencesFooter({ dirty, busy, status, error, onSave }: FooterSaveState & { onSave: () => void }) {
  const api = useContext(FooterApiCtx);

  // Refreshed on every render, and deliberately not in the effect below - the footer must call the
  // handler as it is now, not the one that existed when the tab first registered.
  useEffect(() => {
    if (api) api.saveRef.current = onSave;
  });

  useEffect(() => {
    api?.register({ dirty, busy, status, error });
  }, [api, dirty, busy, status, error]);

  // Unmount only, so switching tabs restores the plain footer. Kept separate from the effect above,
  // whose cleanup would otherwise blank the footer on every value change.
  useEffect(() => () => api?.register(null), [api]);
}

/// The modal's footer: status on the left, Close then Save changes on the right. Save is present only
/// while a tab has registered.
export function PreferencesFooterBar({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("account");
  const state = useContext(FooterStateCtx);
  const api = useContext(FooterApiCtx);

  const statusText =
    state === null || state.error ? null : state.status === "unsaved" ? t("unsavedChanges") : state.status === "saved" ? t("profileSaved") : null;

  return (
    <div className="flex items-center justify-between gap-4 border-t px-5 py-3 dark:border-gray-700">
      <div className="min-w-0 truncate text-[13px]">
        {state?.error ? (
          <span className="text-red-600 dark:text-red-400">{state.error}</span>
        ) : (
          <span className="text-gray-500 dark:text-gray-400">{statusText}</span>
        )}
      </div>
      <div className="flex shrink-0 items-center gap-2">
        <button
          type="button"
          onClick={onClose}
          className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
        >
          {t("common:close")}
        </button>
        {state && (
          <button
            type="button"
            onClick={() => api?.saveRef.current?.()}
            disabled={state.busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {state.busy ? t("common:saving") : t("saveChanges")}
          </button>
        )}
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Add the two new `account` keys to all four catalogues**

`profileSaved` already exists in every catalogue - reuse it for "Saved" rather than adding a key. Add only `unsavedChanges` and `saveChanges`. Edit each file with the Edit tool (not a shell heredoc - a heredoc mangles backslashes on this box).

`apps/web/src/locales/en/account.json`:
```json
  "unsavedChanges": "Unsaved changes",
  "saveChanges": "Save changes",
```
`de`: `"unsavedChanges": "Nicht gespeicherte Änderungen"`, `"saveChanges": "Änderungen speichern"`
`es`: `"unsavedChanges": "Cambios sin guardar"`, `"saveChanges": "Guardar cambios"`
`fr`: `"unsavedChanges": "Modifications non enregistrées"`, `"saveChanges": "Enregistrer les modifications"`

- [ ] **Step 5: Run the tests and verify they pass**

Run: `npm test -- src/components/PreferencesFooter.test.tsx src/locales.test.ts`
Expected: PASS.

- [ ] **Step 6: Write the failing modal tests**

Add to `apps/web/src/components/PreferencesModal.test.tsx`. Replace the existing `RecordingsSection` mock at line 11 with one that registers, so the shell can be exercised without pulling in the real panel. A top-level import is safe here: `PreferencesFooter` does not import `PreferencesModal`, so there is no cycle.

```tsx
import { usePreferencesFooter } from "./PreferencesFooter";

vi.mock("./RecordingsSection", () => ({
  default: () => {
    usePreferencesFooter({ dirty: true, busy: false, status: "unsaved", error: null, onSave: () => {} });
    return <div>RECORDINGS_SECTION</div>;
  },
}));
```

Then add these tests:

```tsx
  it("shows Save changes only on a tab that opts into the footer", () => {
    renderModal();
    // Profile does not register.
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: /^recordings$/i }));
    expect(screen.getByRole("button", { name: /save changes/i })).toBeTruthy();
    expect(screen.getByText("Unsaved changes")).toBeTruthy();

    // Switching away must not leave a stale Save button behind pointing at an unmounted tab.
    fireEvent.click(screen.getByRole("tab", { name: /^profile$/i }));
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();
  });

  it("names the active tab in a breadcrumb beside the dialog title", () => {
    renderModal();
    expect(screen.getByText("/ Profile")).toBeTruthy();
    fireEvent.click(screen.getByRole("tab", { name: /^calendars$/i }));
    expect(screen.getByText("/ Calendars")).toBeTruthy();
  });
```

- [ ] **Step 7: Run them and verify they fail**

Run: `npm test -- src/components/PreferencesModal.test.tsx`
Expected: FAIL - no `Save changes` button, and `Unable to find an element with the text: / Profile`.

- [ ] **Step 8: Wire the modal**

In `apps/web/src/components/PreferencesModal.tsx`:

Add the import:
```tsx
import { PreferencesFooterBar, PreferencesFooterProvider } from "./PreferencesFooter";
```

Replace the header block (lines 119-121) with:
```tsx
          <div className="flex items-baseline gap-2 border-b px-5 pt-4 pb-3 dark:border-gray-700">
            <h2 className="text-base font-semibold dark:text-gray-100">{t("preferencesTitle")}</h2>
            {/* Quiet breadcrumb - says which of six panels you are on without a second heading
                competing with the dialog's own title. Derived from `tabs`, so it cannot drift. */}
            <span className="text-[13px] text-gray-500 dark:text-gray-400">
              / {tabs.find((x) => x.id === tab)?.label}
            </span>
          </div>
```

Wrap the whole right-hand panel (the `<div className="flex min-w-0 flex-1 flex-col">` at line 118 through its closing tag) in `<PreferencesFooterProvider>...</PreferencesFooterProvider>`, and replace the footer block (lines 130-138) with:
```tsx
          <PreferencesFooterBar onClose={onClose} />
```

The provider must enclose both the content pane and the bar, or the bar cannot see what the tab registered.

- [ ] **Step 9: Run the full suite**

Run: `npm test`
Expected: PASS. The pre-existing `does not close on a backdrop click, but Close does` test must still pass - `PreferencesFooterBar` keeps a Close button with the same accessible name.

- [ ] **Step 10: Commit**

```bash
git add apps/web/src/components/PreferencesFooter.tsx apps/web/src/components/PreferencesFooter.test.tsx apps/web/src/components/PreferencesModal.tsx apps/web/src/components/PreferencesModal.test.tsx apps/web/src/locales/en/account.json apps/web/src/locales/de/account.json apps/web/src/locales/es/account.json apps/web/src/locales/fr/account.json
git commit -m "feat(web): let a Preferences tab put its Save in the modal footer"
```

---

### Task 2: Recordings panel adopts the footer

Moves the panel's Save without changing how it looks otherwise, so the payload assertions stay readable and any regression here is isolated from the visual rebuild.

**Files:**
- Modify: `apps/web/src/components/RecordingsSection.tsx` (state block lines 30-39, `onSave` lines 55-74, the Save block lines 177-188)
- Modify: `apps/web/src/components/RecordingsSection.test.tsx`

**Interfaces:**
- Consumes: `usePreferencesFooter`, `PreferencesFooterProvider`, `PreferencesFooterBar` from Task 1.
- Produces: nothing new for later tasks; later tasks keep calling `usePreferencesFooter` from inside `RecordingsSection`.

- [ ] **Step 1: Write the failing tests**

In `apps/web/src/components/RecordingsSection.test.tsx`, replace `renderSection` with a harness that mounts the panel the way the modal does:

```tsx
import { PreferencesFooterProvider, PreferencesFooterBar } from "./PreferencesFooter";

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <PreferencesFooterProvider>
        <RecordingsSection />
        <PreferencesFooterBar onClose={() => {}} />
      </PreferencesFooterProvider>
    </QueryClientProvider>,
  );
}

/// The panel has no Save of its own any more - it registers one with the modal footer.
const saveButton = () => screen.getByRole("button", { name: /save changes/i });
```

Then replace every `screen.getByRole("button", { name: /^save$/i })` in the file with `saveButton()`, and add:

```tsx
  it("has no Save of its own - it registers one with the modal footer", async () => {
    renderSection();
    await screen.findByRole("radio", { name: /currently selected folder/i });
    expect(screen.queryByRole("button", { name: /^save$/i })).toBeNull();
    expect(saveButton()).toBeTruthy();
  });

  it("reports unsaved changes to the footer, and clears them on a successful save", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    expect(screen.getByText("Unsaved changes")).toBeTruthy();

    fireEvent.click(saveButton());
    await waitFor(() => expect(screen.getByText("Saved")).toBeTruthy());
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });

  it("clears the unsaved indicator when an edit is undone by hand", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    expect(screen.getByText("Unsaved changes")).toBeTruthy();

    // Back to the value that was loaded - there is nothing to save, so the footer must say so.
    fireEvent.click(screen.getByRole("radio", { name: /currently selected folder/i }));
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });

  it("surfaces a save failure in the footer", async () => {
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("boom"));
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    fireEvent.click(saveButton());
    await waitFor(() => expect(screen.getByText(/boom/)).toBeTruthy());
  });
```

- [ ] **Step 2: Run them and verify they fail**

Run: `npm test -- src/components/RecordingsSection.test.tsx`
Expected: FAIL - `Unable to find an accessible element with the role "button" and name /save changes/i`.

- [ ] **Step 3: Rework the panel's save**

In `apps/web/src/components/RecordingsSection.tsx`:

Add the import:
```tsx
import { usePreferencesFooter } from "./PreferencesFooter";
```

Replace the `saved` state (line 38) with a baseline snapshot, and add the derived dirty flag. Put this after the existing state declarations:

```tsx
/// The five values as last loaded or last saved. `dirty` is a comparison against this rather than a flag
/// set by each change handler, so undoing an edit by hand clears the indicator instead of latching it.
/// Reset from the payload on a successful save rather than waiting for the refetch, which would otherwise
/// leave the footer briefly reading "Unsaved changes" over values that are already stored.
interface Baseline {
  placementMode: RecordingPlacementMode;
  placementSectionId: string | null;
  calendarAutoStop: boolean;
  afterMinutes: number;
  silenceSeconds: number;
}
```

```tsx
  const [baseline, setBaseline] = useState<Baseline | null>(null);
```

Delete `const [saved, setSaved] = useState(false);`.

Extend the seeding effect (lines 41-49) to set the baseline too:
```tsx
  useEffect(() => {
    if (data) {
      const next: Baseline = {
        placementMode: data.placementMode ?? "SelectedFolder",
        placementSectionId: data.placementSectionId ?? null,
        calendarAutoStop: data.calendarAutoStopEnabled ?? false,
        afterMinutes: data.calendarAutoStopAfterMinutes ?? DEFAULT_AFTER_MINUTES,
        silenceSeconds: data.calendarSilenceStopSeconds ?? DEFAULT_SILENCE_SECONDS,
      };
      setPlacementMode(next.placementMode);
      setPlacementSectionId(next.placementSectionId);
      setCalendarAutoStop(next.calendarAutoStop);
      setAfterMinutes(String(next.afterMinutes));
      setSilenceSeconds(String(next.silenceSeconds));
      setBaseline(next);
    }
  }, [data]);
```

Add the current-payload helper and the dirty comparison, above the `if (!data) return null` guard so the hook below runs unconditionally:

```tsx
  // The exact five fields Save sends, so `dirty` compares what would be stored rather than what is typed:
  // blanking a duration field is not a change, because `positiveOr` would store the same number anyway.
  const current: Baseline = {
    placementMode,
    placementSectionId: placementMode === "SpecificFolder" ? placementSectionId : null,
    calendarAutoStop,
    afterMinutes: positiveOr(afterMinutes, DEFAULT_AFTER_MINUTES),
    silenceSeconds: positiveOr(silenceSeconds, DEFAULT_SILENCE_SECONDS),
  };
  const dirty =
    baseline !== null &&
    (current.placementMode !== baseline.placementMode ||
      current.placementSectionId !== baseline.placementSectionId ||
      current.calendarAutoStop !== baseline.calendarAutoStop ||
      current.afterMinutes !== baseline.afterMinutes ||
      current.silenceSeconds !== baseline.silenceSeconds);

  usePreferencesFooter({
    dirty,
    busy,
    status: dirty ? "unsaved" : savedOnce ? "saved" : "idle",
    error,
    onSave,
  });
```

Add `const [savedOnce, setSavedOnce] = useState(false);` alongside the other state.

**Hooks order matters:** `usePreferencesFooter` must be called before the `if (!data) return null` early return, or React throws when the query resolves. `onSave` and `current` are plain function/const declarations, so they are available; move the `if (!data) return null` guard to sit *after* the `usePreferencesFooter` call. `onSave` is a function declaration and is hoisted, so it can be referenced above its definition.

Rewrite `onSave` to reset the baseline:
```tsx
  async function onSave() {
    setError(null);
    setBusy(true);
    try {
      await api.updateUserSettings({
        placementMode: current.placementMode,
        placementSectionId: current.placementSectionId,
        calendarAutoStopEnabled: current.calendarAutoStop,
        calendarAutoStopAfterMinutes: current.afterMinutes,
        calendarSilenceStopSeconds: current.silenceSeconds,
      });
      qc.invalidateQueries({ queryKey: ["user-settings"] });
      // Show the coerced values, so a field left blank reads as the default that was actually stored.
      setAfterMinutes(String(current.afterMinutes));
      setSilenceSeconds(String(current.silenceSeconds));
      setBaseline(current);
      setSavedOnce(true);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }
```

Delete the whole trailing Save block (lines 177-188).

- [ ] **Step 4: Run the tests and verify they pass**

Run: `npm test -- src/components/RecordingsSection.test.tsx`
Expected: PASS, including every pre-existing payload assertion.

- [ ] **Step 5: Verify the payload is byte-identical**

Run: `npm test -- src/components/RecordingsSection.test.tsx -t "and nothing else"`
Expected: PASS. This is the test that asserts the exact object shape; if it fails, the refactor changed the contract.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/RecordingsSection.tsx apps/web/src/components/RecordingsSection.test.tsx
git commit -m "refactor(web): move the Recordings tab's Save into the Preferences footer"
```

---

### Task 3: Placement choice cards

**Files:**
- Modify: `apps/web/src/components/RecordingsSection.tsx` (the intro paragraph and `<fieldset>`, lines 77-110)
- Modify: `apps/web/src/components/RecordingsSection.test.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`

**Interfaces:**
- Consumes: `RecordingPlacementMode` from `../lib/types` (already imported).
- Produces: nothing consumed by later tasks.

The inline `FolderPicker` under the `SpecificFolder` card stays exactly as it is in this task; Task 5 replaces it.

- [ ] **Step 1: Write the failing tests**

In `RecordingsSection.test.tsx`, update the three existing radio queries to the new names and add the card tests. Replace `/currently selected folder/i` with `/the folder I'm looking at/i` and `/specific folder/i` with `/one fixed folder/i` **everywhere in the file**, then add:

```tsx
  it("heads the placement group and says what each choice does", async () => {
    renderSection();
    await screen.findByText("Where a new recording is filed");
    expect(screen.getByText("in your personal space")).toBeTruthy();

    expect(screen.getByText("Files into whichever folder is open in the list when you start recording.")).toBeTruthy();
    expect(screen.getByText("Everything lands in one place; file it into a folder afterwards.")).toBeTruthy();
    expect(screen.getByText("Always the same folder, wherever you happen to be.")).toBeTruthy();
  });

  it("marks the open-folder choice as the default", async () => {
    renderSection();
    const card = (await screen.findByRole("radio", { name: /the folder I'm looking at/i })).closest("label");
    expect(card?.textContent).toContain("Default");
  });

  it("keeps the three choices in one radio group so arrow keys still work", async () => {
    renderSection();
    await screen.findByText("Where a new recording is filed");
    const names = screen.getAllByRole("radio").map((r) => (r as HTMLInputElement).name);
    expect(new Set(names)).toEqual(new Set(["placement-mode"]));
  });
```

- [ ] **Step 2: Run them and verify they fail**

Run: `npm test -- src/components/RecordingsSection.test.tsx`
Expected: FAIL - `Unable to find an element with the text: Where a new recording is filed`.

- [ ] **Step 3: Add the `en` copy**

In `apps/web/src/locales/en/account.json`, **change** these values:
```json
  "placementUngrouped": "Always Ungrouped",
  "placementSelected": "The folder I'm looking at",
  "placementSpecific": "One fixed folder",
```
and **add**:
```json
  "placementHeading": "Where a new recording is filed",
  "placementHeadingMeta": "in your personal space",
  "placementDefaultChip": "Default",
  "placementSelectedMeta": "Files into whichever folder is open in the list when you start recording.",
  "placementUngroupedMeta": "Everything lands in one place; file it into a folder afterwards.",
  "placementSpecificMeta": "Always the same folder, wherever you happen to be.",
```
**Remove** `"recordingsIntro"`.

- [ ] **Step 4: Mirror the copy into de, es and fr**

`de` - change `placementUngrouped` to `"Immer Nicht gruppiert"`, `placementSelected` to `"Der Ordner, den ich gerade ansehe"`, `placementSpecific` to `"Ein fester Ordner"`; remove `recordingsIntro`; add:
```json
  "placementHeading": "Wo eine neue Aufnahme abgelegt wird",
  "placementHeadingMeta": "in Ihrem persönlichen Bereich",
  "placementDefaultChip": "Standard",
  "placementSelectedMeta": "Legt die Aufnahme in dem Ordner ab, der beim Start der Aufnahme in der Liste geöffnet ist.",
  "placementUngroupedMeta": "Alles landet an einem Ort; ordnen Sie es danach einem Ordner zu.",
  "placementSpecificMeta": "Immer derselbe Ordner, wo auch immer Sie sich befinden.",
```

`es` - `placementUngrouped` to `"Siempre Sin agrupar"`, `placementSelected` to `"La carpeta que estoy viendo"`, `placementSpecific` to `"Una carpeta fija"`; remove `recordingsIntro`; add:
```json
  "placementHeading": "Dónde se archiva una nueva grabación",
  "placementHeadingMeta": "en tu espacio personal",
  "placementDefaultChip": "Predeterminado",
  "placementSelectedMeta": "Archiva en la carpeta que esté abierta en la lista cuando inicias la grabación.",
  "placementUngroupedMeta": "Todo llega a un mismo sitio; archívalo en una carpeta después.",
  "placementSpecificMeta": "Siempre la misma carpeta, estés donde estés.",
```

`fr` - `placementUngrouped` to `"Toujours Non groupé"`, `placementSelected` to `"Le dossier que je consulte"`, `placementSpecific` to `"Un dossier fixe"`; remove `recordingsIntro`; add:
```json
  "placementHeading": "Où un nouvel enregistrement est classé",
  "placementHeadingMeta": "dans votre espace personnel",
  "placementDefaultChip": "Par défaut",
  "placementSelectedMeta": "Classe dans le dossier ouvert dans la liste au moment où vous lancez l'enregistrement.",
  "placementUngroupedMeta": "Tout arrive au même endroit ; classez-le dans un dossier ensuite.",
  "placementSpecificMeta": "Toujours le même dossier, où que vous soyez.",
```

- [ ] **Step 5: Replace the radio list with cards**

In `RecordingsSection.tsx`, delete the intro `<p>` (line 78) and replace the `<fieldset>` (lines 79-110) with:

```tsx
      <div className="flex items-baseline gap-2">
        <h3 className="text-[15px] font-semibold dark:text-gray-100">{t("placementHeading")}</h3>
        <span className="text-xs text-gray-500 dark:text-gray-400">{t("placementHeadingMeta")}</span>
      </div>

      <fieldset className="flex flex-col gap-2.5">
        {(
          [
            { mode: "SelectedFolder", title: "placementSelected", meta: "placementSelectedMeta", isDefault: true },
            { mode: "Ungrouped", title: "placementUngrouped", meta: "placementUngroupedMeta" },
            { mode: "SpecificFolder", title: "placementSpecific", meta: "placementSpecificMeta" },
          ] as const
        ).map((card) => (
          <label
            key={card.mode}
            // The selected state is the card's OWN border and background, never an outset ring. The
            // content pane scrolls, and a ring painted 1px outside the box makes the pane wider than its
            // client width, which paints a full-width horizontal scrollbar across the whole panel.
            className={`cursor-pointer rounded-lg border px-3.5 py-3 ${
              placementMode === card.mode
                ? "border-blue-500/60 bg-blue-500/[.07] dark:border-blue-500/60 dark:bg-blue-500/[.14]"
                : "border-gray-200 dark:border-gray-700"
            }`}
          >
            <div className="flex items-start gap-3">
              <input
                type="radio"
                name="placement-mode"
                className="mt-0.5 accent-blue-600"
                checked={placementMode === card.mode}
                onChange={() => setPlacementMode(card.mode)}
              />
              <div className="flex min-w-0 flex-col gap-[3px]">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-gray-900 dark:text-gray-100">{t(card.title)}</span>
                  {card.isDefault && (
                    <span className="rounded border border-blue-500/40 bg-blue-500/10 px-1.5 py-px text-[10px] uppercase tracking-[.06em] text-blue-700 dark:text-blue-200">
                      {t("placementDefaultChip")}
                    </span>
                  )}
                </div>
                <span className="text-[13px] text-gray-500 dark:text-gray-400">{t(card.meta)}</span>
              </div>
            </div>
          </label>
        ))}
      </fieldset>
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `npm test -- src/components/RecordingsSection.test.tsx src/locales.test.ts`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/RecordingsSection.tsx apps/web/src/components/RecordingsSection.test.tsx apps/web/src/locales/en/account.json apps/web/src/locales/de/account.json apps/web/src/locales/es/account.json apps/web/src/locales/fr/account.json
git commit -m "feat(web): make each recording-placement choice a card that says what it does"
```

---

### Task 4: Calendar auto-stop switch, sentence rows and worked example

**Files:**
- Modify: `apps/web/src/components/RecordingsSection.tsx` (the calendar block, lines 128-175 of the original file)
- Modify: `apps/web/src/components/RecordingsSection.test.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/account.json`

**Interfaces:**
- Consumes: `CalendarIcon` from `./icons` (already exported).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing tests**

In `RecordingsSection.test.tsx`, replace the whole `describe("recording from a calendar event")` block with:

```tsx
  describe("recording from a calendar event", () => {
    const autoStop = () => screen.getByRole("switch", { name: /let a calendar meeting end its own recording/i });
    const afterMinutes = () => screen.getByLabelText(/minutes after the meeting ends/i) as HTMLInputElement;
    const silenceSeconds = () => screen.getByLabelText(/seconds of silence/i) as HTMLInputElement;

    it("shows the card with the switch off and no duration fields at all", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

      expect(autoStop().getAttribute("aria-checked")).toBe("false");
      // Absent, not disabled: a field you cannot use should not be on screen looking editable.
      expect(screen.queryByLabelText(/minutes after the meeting ends/i)).toBeNull();
      expect(screen.queryByLabelText(/seconds of silence/i)).toBeNull();
    });

    it("says the option applies only to a recording started from the calendar", async () => {
      renderSection();
      expect(
        await screen.findByText(
          /Only when you join the meeting from your calendar.*not cut short by this option\./,
        ),
      ).toBeTruthy();
    });

    it("reveals both durations at their defaults when the switch is turned on", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

      fireEvent.click(autoStop());
      expect(autoStop().getAttribute("aria-checked")).toBe("true");
      expect(afterMinutes().value).toBe("3");
      expect(silenceSeconds().value).toBe("30");
    });

    it("works the example through from the two values, and recomputes it live", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");
      fireEvent.click(autoStop());

      expect(
        screen.getByText(
          "A 10:00-11:00 meeting keeps recording until 11:03 - or stops sooner, once 30 seconds pass with nobody speaking.",
        ),
      ).toBeTruthy();

      fireEvent.change(afterMinutes(), { target: { value: "90" } });
      fireEvent.change(silenceSeconds(), { target: { value: "45" } });
      expect(
        screen.getByText(
          "A 10:00-11:00 meeting keeps recording until 12:30 - or stops sooner, once 45 seconds pass with nobody speaking.",
        ),
      ).toBeTruthy();
    });

    it("announces the example when a value changes", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");
      fireEvent.click(autoStop());
      expect(screen.getByText(/keeps recording until/).getAttribute("aria-live")).toBe("polite");
    });

    it("saves the three settings alongside the placement", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

      fireEvent.click(autoStop());
      fireEvent.change(afterMinutes(), { target: { value: "10" } });
      fireEvent.change(silenceSeconds(), { target: { value: "90" } });
      fireEvent.click(saveButton());

      await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
      expect((api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0]).toEqual({
        placementMode: "SelectedFolder", placementSectionId: null,
        calendarAutoStopEnabled: true, calendarAutoStopAfterMinutes: 10, calendarSilenceStopSeconds: 90,
      });
    });

    it("seeds the controls from saved settings (round-trip)", async () => {
      (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
        ...settings, calendarAutoStopEnabled: true, calendarAutoStopAfterMinutes: 7,
        calendarSilenceStopSeconds: 45,
      });
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

      expect(autoStop().getAttribute("aria-checked")).toBe("true");
      expect(afterMinutes().value).toBe("7");
      expect(silenceSeconds().value).toBe("45");
    });

    it("sends the defaults rather than a blanked or zero duration", async () => {
      // Clearing a number input yields "" - saving that as 0 would stop a recording the instant it began.
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

      fireEvent.click(autoStop());
      fireEvent.change(afterMinutes(), { target: { value: "" } });
      fireEvent.change(silenceSeconds(), { target: { value: "0" } });
      fireEvent.click(saveButton());

      await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
      const arg = (api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0];
      expect(arg.calendarAutoStopAfterMinutes).toBe(3);
      expect(arg.calendarSilenceStopSeconds).toBe(30);
    });
  });
```

- [ ] **Step 2: Run them and verify they fail**

Run: `npm test -- src/components/RecordingsSection.test.tsx`
Expected: FAIL - `Unable to find an accessible element with the role "switch"`.

- [ ] **Step 3: Add the `en` copy**

In `apps/web/src/locales/en/account.json`, **add**:
```json
  "calendarAutoStopHeading": "Let a calendar meeting end its own recording",
  "calendarAutoStopBody": "Only when you join the meeting from your calendar, so its end time is known. A recording you start with the Record button is not cut short by this option.",
  "calendarStopPrefix": "Stop",
  "calendarStopMinutesSuffix": "minutes after the meeting was due to finish,",
  "calendarSilencePrefix": "or after",
  "calendarSilenceSuffix": "seconds of silence - whichever comes first.",
  "calendarAutoStopExample": "A 10:00-11:00 meeting keeps recording until {{until}} - or stops sooner, once {{seconds}} seconds pass with nobody speaking.",
```
**Remove** `"calendarRecordingHeading"`, `"calendarRecordingIntro"`, `"calendarAutoStop"`, `"calendarAfterMinutesHint"`, `"calendarSilenceSecondsHint"`.

**Keep** `"calendarAfterMinutes"` and `"calendarSilenceSeconds"` - they are no longer visible labels but are the two inputs' `aria-label`s, which is what the tests above query by.

- [ ] **Step 4: Mirror into de, es and fr**

Same removals in each. Additions:

`de`:
```json
  "calendarAutoStopHeading": "Eine Kalenderbesprechung ihre eigene Aufnahme beenden lassen",
  "calendarAutoStopBody": "Nur wenn Sie der Besprechung über Ihren Kalender beitreten, sodass deren Endzeit bekannt ist. Eine Aufnahme, die Sie mit der Aufnahmeschaltfläche starten, wird dadurch nicht verkürzt.",
  "calendarStopPrefix": "Beenden",
  "calendarStopMinutesSuffix": "Minuten nach dem geplanten Ende der Besprechung,",
  "calendarSilencePrefix": "oder nach",
  "calendarSilenceSuffix": "Sekunden Stille - je nachdem, was zuerst eintritt.",
  "calendarAutoStopExample": "Eine Besprechung von 10:00-11:00 nimmt bis {{until}} weiter auf - oder stoppt früher, sobald {{seconds}} Sekunden lang niemand spricht.",
```

`es`:
```json
  "calendarAutoStopHeading": "Dejar que una reunión del calendario termine su propia grabación",
  "calendarAutoStopBody": "Solo cuando te unes a la reunión desde tu calendario, de modo que se conoce su hora de fin. Una grabación que inicias con el botón Grabar no se corta por esta opción.",
  "calendarStopPrefix": "Detener",
  "calendarStopMinutesSuffix": "minutos después de la hora prevista de fin de la reunión,",
  "calendarSilencePrefix": "o tras",
  "calendarSilenceSuffix": "segundos de silencio - lo que ocurra primero.",
  "calendarAutoStopExample": "Una reunión de 10:00-11:00 sigue grabando hasta las {{until}} - o se detiene antes, en cuanto pasan {{seconds}} segundos sin que nadie hable.",
```

`fr`:
```json
  "calendarAutoStopHeading": "Laisser une réunion du calendrier terminer son propre enregistrement",
  "calendarAutoStopBody": "Uniquement lorsque vous rejoignez la réunion depuis votre calendrier, afin que son heure de fin soit connue. Un enregistrement lancé avec le bouton Enregistrer n'est pas écourté par cette option.",
  "calendarStopPrefix": "Arrêter",
  "calendarStopMinutesSuffix": "minutes après l'heure de fin prévue de la réunion,",
  "calendarSilencePrefix": "ou après",
  "calendarSilenceSuffix": "secondes de silence - selon ce qui arrive en premier.",
  "calendarAutoStopExample": "Une réunion de 10:00-11:00 continue d'enregistrer jusqu'à {{until}} - ou s'arrête plus tôt, dès que {{seconds}} secondes passent sans que personne ne parle.",
```

- [ ] **Step 5: Replace the calendar block**

Add `CalendarIcon` to the icons import in `RecordingsSection.tsx`:
```tsx
import { CalendarIcon } from "./icons";
```

Add this pure helper above the component, beside `positiveOr`:

```tsx
/// 24h `HH:MM`, `addMinutes` after the given whole hour. Only used by the worked example, whose meeting is
/// a fixed 10:00-11:00, so it takes an hour rather than a date; wraps past midnight so a silly value like
/// 3000 minutes still renders a clock time rather than "35:00".
function clockAfter(hour: number, addMinutes: number): string {
  const total = (hour * 60 + addMinutes) % (24 * 60);
  const h = Math.floor(total / 60);
  const m = total % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}
```

Replace the whole calendar `<div className="space-y-2 border-t pt-3 ...">` block with:

```tsx
      {/* Recording started from a calendar event: the only case where the meeting's end time is known, so
          the only case where the recorder can end a take by itself. */}
      <div className="overflow-hidden rounded-lg border dark:border-gray-700">
        <div className="flex items-start justify-between gap-5 px-4 py-3.5">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <span className="shrink-0 text-gray-500 dark:text-gray-400">
                <CalendarIcon size={14} />
              </span>
              <h3 className="text-[15px] font-semibold dark:text-gray-100">{t("calendarAutoStopHeading")}</h3>
            </div>
            <p className="mt-1 text-[13px] text-pretty text-gray-500 dark:text-gray-400">{t("calendarAutoStopBody")}</p>
          </div>
          {/* A native checkbox cannot be styled as a track and knob without hiding it, which loses the
              focus ring; `role="switch"` on a button is the same semantics with a real focusable target.
              The heading is its accessible name - the control has no visible label of its own. */}
          <button
            type="button"
            role="switch"
            aria-checked={calendarAutoStop}
            aria-label={t("calendarAutoStopHeading")}
            onClick={() => setCalendarAutoStop((on) => !on)}
            className={`relative h-6 w-11 shrink-0 rounded-full transition-colors ${
              calendarAutoStop ? "bg-blue-600" : "bg-gray-300 dark:bg-gray-700"
            }`}
          >
            <span
              aria-hidden
              className={`absolute top-[3px] h-[18px] w-[18px] rounded-full bg-white transition-[left] ${
                calendarAutoStop ? "left-[23px]" : "left-[3px]"
              }`}
            />
          </button>
        </div>

        {/* Absent rather than disabled: the two durations say HOW a recording ends, and there is nothing
            for them to qualify while the switch is off. */}
        {calendarAutoStop && (
          <div className="flex flex-col gap-3 border-t bg-gray-50 px-4 py-3.5 dark:border-gray-700 dark:bg-white/[.02]">
            <div className="flex flex-wrap items-center gap-2 text-sm dark:text-gray-200">
              <span>{t("calendarStopPrefix")}</span>
              <input
                type="number"
                min={1}
                step={1}
                value={afterMinutes}
                onChange={(e) => setAfterMinutes(e.target.value)}
                aria-label={t("calendarAfterMinutes")}
                className="w-[60px] rounded border px-2 py-1.5 text-center text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              />
              <span>{t("calendarStopMinutesSuffix")}</span>
            </div>
            <div className="flex flex-wrap items-center gap-2 text-sm dark:text-gray-200">
              <span>{t("calendarSilencePrefix")}</span>
              <input
                type="number"
                min={1}
                step={1}
                value={silenceSeconds}
                onChange={(e) => setSilenceSeconds(e.target.value)}
                aria-label={t("calendarSilenceSeconds")}
                className="w-[60px] rounded border px-2 py-1.5 text-center text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              />
              <span>{t("calendarSilenceSuffix")}</span>
            </div>
            {/* The two per-field hints this replaces said what each number was for in the abstract; one
                worked example says it once, in the reader's own numbers. */}
            <p
              aria-live="polite"
              className="border-l-2 border-blue-500/50 pl-3 text-[13px] text-gray-500 dark:text-gray-400"
            >
              {t("calendarAutoStopExample", {
                until: clockAfter(11, positiveOr(afterMinutes, DEFAULT_AFTER_MINUTES)),
                seconds: positiveOr(silenceSeconds, DEFAULT_SILENCE_SECONDS),
              })}
            </p>
          </div>
        )}
      </div>
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `npm test -- src/components/RecordingsSection.test.tsx src/locales.test.ts`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/RecordingsSection.tsx apps/web/src/components/RecordingsSection.test.tsx apps/web/src/locales/en/account.json apps/web/src/locales/de/account.json apps/web/src/locales/es/account.json apps/web/src/locales/fr/account.json
git commit -m "feat(web): fold calendar auto-stop into one switch with a worked example"
```

---

### Task 5: The folder picker dialog

**Files:**
- Create: `apps/web/src/components/FolderPickerModal.tsx`
- Create: `apps/web/src/components/FolderPickerModal.test.tsx`
- Modify: `apps/web/src/components/RecordingsSection.tsx` (the `SpecificFolder` reveal, lines 111-126 of the original file)
- Modify: `apps/web/src/components/RecordingsSection.test.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json` and `.../account.json`

**Interfaces:**
- Consumes: `FolderPicker` (unchanged), `orderedSections` from `../lib/sectionTree`, `SectionDto` from `../lib/types`.
- Produces:
  ```tsx
  export default function FolderPickerModal(props: {
    sections: SectionDto[];
    selectedId: string | null;
    onSelect: (id: string | null) => void;
    onClose: () => void;
  }): JSX.Element
  ```

- [ ] **Step 1: Write the failing dialog tests**

Create `apps/web/src/components/FolderPickerModal.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import FolderPickerModal from "./FolderPickerModal";

const sections = [
  { id: "customers", name: "Customers", parentId: null, position: 0 },
  { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
];

function renderPicker(props: Partial<Parameters<typeof FolderPickerModal>[0]> = {}) {
  const onSelect = vi.fn();
  const onClose = vi.fn();
  render(
    <FolderPickerModal
      sections={sections}
      selectedId={props.selectedId ?? null}
      onSelect={props.onSelect ?? onSelect}
      onClose={props.onClose ?? onClose}
    />,
  );
  return { onSelect, onClose };
}

describe("FolderPickerModal", () => {
  it("names itself and says what the choice is for", () => {
    renderPicker();
    expect(screen.getByRole("dialog", { name: "Choose a folder" })).toBeTruthy();
    expect(screen.getByText("Every new recording will be filed here.")).toBeTruthy();
  });

  it("puts the caret in the filter box on open, so typing works without a click", () => {
    renderPicker();
    expect(document.activeElement).toBe(screen.getByLabelText("Filter folders"));
  });

  it("shows the chosen folder's full path, and Ungrouped for the root", () => {
    const { rerender } = render(
      <FolderPickerModal sections={sections} selectedId="acme" onSelect={() => {}} onClose={() => {}} />,
    );
    expect(screen.getByText(/Chosen:/).textContent).toContain("Customers › Acme Corp");

    rerender(<FolderPickerModal sections={sections} selectedId={null} onSelect={() => {}} onClose={() => {}} />);
    expect(screen.getByText(/Chosen:/).textContent).toContain("Ungrouped");
  });

  it("closes on Done and on the close control", () => {
    const a = renderPicker();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(a.onClose).toHaveBeenCalled();

    const b = renderPicker();
    fireEvent.click(screen.getAllByRole("button", { name: "Close folder picker" })[0]);
    expect(b.onClose).toHaveBeenCalled();
  });

  // The Preferences modal listens for Escape on `document`. If this dialog let Escape through, one press
  // would close both, throwing the user out of Preferences to dismiss a picker.
  it("swallows Escape so an enclosing modal does not close too", () => {
    const outer = vi.fn();
    document.addEventListener("keydown", outer);
    try {
      const { onClose } = renderPicker();
      fireEvent.keyDown(screen.getByLabelText("Filter folders"), { key: "Escape" });
      expect(onClose).toHaveBeenCalled();
      expect(outer).not.toHaveBeenCalled();
    } finally {
      document.removeEventListener("keydown", outer);
    }
  });

  it("lets a non-empty filter take Escape for itself, closing nothing", () => {
    const { onClose } = renderPicker();
    const filter = screen.getByLabelText("Filter folders");
    fireEvent.change(filter, { target: { value: "acme" } });
    fireEvent.keyDown(filter, { key: "Escape" });
    expect((filter as HTMLInputElement).value).toBe("");
    expect(onClose).not.toHaveBeenCalled();
  });

  it("passes a choice straight through - it is applied to the panel, not held here", () => {
    const { onSelect } = renderPicker();
    fireEvent.click(screen.getByLabelText("Select Ungrouped"));
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  // Moved here from RecordingsSection.test.tsx, where the picker used to be inline. Real Tab presses
  // only, no `.focus()` shortcut - this proves the whole chain is reachable by keyboard, not merely that
  // each target is focusable. `FolderPicker` costs 2 stops per drillable row by design; the point of this
  // test is that the dialog chrome does not break that chain.
  it("is keyboard operable from the filter box through to a folder row", async () => {
    const user = userEvent.setup();
    const { onSelect } = renderPicker();

    expect(document.activeElement).toBe(screen.getByLabelText("Filter folders"));
    await user.tab(); // the root "Ungrouped" row
    expect(document.activeElement).toBe(screen.getByLabelText("Select Ungrouped"));
    await user.tab(); // "Customers" row body (drills, does not choose)
    expect(document.activeElement).toBe(screen.getByLabelText("Open Customers"));
    await user.tab(); // "Customers" row's separate select control
    expect(document.activeElement).toBe(screen.getByLabelText("Select Customers"));

    await user.keyboard("{Enter}");
    expect(onSelect).toHaveBeenCalledWith("customers");
  });
});
```

Add `import userEvent from "@testing-library/user-event";` at the top of the file.

- [ ] **Step 2: Run them and verify they fail**

Run: `npm test -- src/components/FolderPickerModal.test.tsx`
Expected: FAIL - `Failed to resolve import "./FolderPickerModal"`.

- [ ] **Step 3: Add the picker chrome copy**

In `apps/web/src/locales/en/workspace.json` **add**:
```json
  "folderPickerTitle": "Choose a folder",
  "folderPickerSubtitle": "Every new recording will be filed here.",
  "folderPickerChosen": "Chosen: {{path}}",
  "folderPickerDone": "Done",
  "folderPickerCloseAria": "Close folder picker",
```

`de`:
```json
  "folderPickerTitle": "Ordner auswählen",
  "folderPickerSubtitle": "Jede neue Aufnahme wird hier abgelegt.",
  "folderPickerChosen": "Ausgewählt: {{path}}",
  "folderPickerDone": "Fertig",
  "folderPickerCloseAria": "Ordnerauswahl schließen",
```

`es`:
```json
  "folderPickerTitle": "Elegir una carpeta",
  "folderPickerSubtitle": "Cada nueva grabación se archivará aquí.",
  "folderPickerChosen": "Elegida: {{path}}",
  "folderPickerDone": "Hecho",
  "folderPickerCloseAria": "Cerrar el selector de carpetas",
```

`fr`:
```json
  "folderPickerTitle": "Choisir un dossier",
  "folderPickerSubtitle": "Chaque nouvel enregistrement sera classé ici.",
  "folderPickerChosen": "Choisi : {{path}}",
  "folderPickerDone": "Terminé",
  "folderPickerCloseAria": "Fermer le sélecteur de dossiers",
```

- [ ] **Step 4: Write `FolderPickerModal.tsx`**

```tsx
import { useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";
import { orderedSections } from "../lib/sectionTree";
import type { SectionDto } from "../lib/types";
import FolderPicker from "./FolderPicker";

/// Dialog chrome around `FolderPicker`, which is rendered unchanged - the row semantics stay the ones the
/// left nav teaches (a row body drills, a separate control chooses), and `MoveToSectionModal`, the other
/// consumer, is untouched by this.
///
/// The dialog holds no choice of its own: `onSelect` fires straight through to the panel, and Done only
/// closes. The panel's Save is still the only thing that persists anything.
export default function FolderPickerModal({
  sections,
  selectedId,
  onSelect,
  onClose,
}: {
  sections: SectionDto[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation("workspace");
  const boxRef = useRef<HTMLDivElement>(null);

  // The filter box is the intended keyboard path through a long tree, so start there rather than making
  // the user tab past the header to reach it.
  useEffect(() => {
    boxRef.current?.querySelector<HTMLInputElement>('input[type="text"]')?.focus();
  }, []);

  const chosen = useMemo(() => {
    if (selectedId === null) return t("ungrouped");
    return orderedSections(sections).find((o) => o.section.id === selectedId)?.label ?? t("ungrouped");
  }, [sections, selectedId, t]);

  // `PreferencesModal` listens for Escape on `document`. React delegates from the root container, which is
  // a descendant of `document`, so stopping propagation on the native event here does prevent that
  // listener - one Escape closes this dialog and leaves Preferences open. When the filter box is
  // non-empty `FolderPicker` stops the event first to clear the filter, so that press reaches neither.
  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key !== "Escape") return;
    e.stopPropagation();
    e.nativeEvent.stopImmediatePropagation();
    onClose();
  }

  return (
    // The backdrop does NOT close on click, matching every other dialog in this app.
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-gray-950/55 p-4" onKeyDown={onKeyDown}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t("folderPickerTitle")}
        ref={boxRef}
        className="flex max-h-full w-[420px] flex-col overflow-hidden rounded-[10px] border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-start justify-between gap-3 px-4 pt-3.5 pb-2.5">
          <div className="min-w-0">
            <h2 className="text-[15px] font-semibold dark:text-gray-100">{t("folderPickerTitle")}</h2>
            <p className="text-xs text-gray-500 dark:text-gray-400">{t("folderPickerSubtitle")}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("folderPickerCloseAria")}
            className="shrink-0 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
          >
            ✕
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-4">
          <FolderPicker sections={sections} selectedId={selectedId} onSelect={onSelect} />
        </div>

        <div className="flex items-center justify-between gap-3 border-t px-4 py-2.5 dark:border-gray-700">
          <span className="min-w-0 truncate text-xs text-gray-500 dark:text-gray-400">
            {t("folderPickerChosen", { path: chosen })}
          </span>
          <button
            type="button"
            onClick={onClose}
            className="shrink-0 rounded bg-gray-900 px-3.5 py-1.5 text-[13px] font-medium text-white dark:bg-gray-100 dark:text-gray-900"
          >
            {t("folderPickerDone")}
          </button>
        </div>
      </div>
    </div>
  );
}
```

- [ ] **Step 5: Run the dialog tests and verify they pass**

Run: `npm test -- src/components/FolderPickerModal.test.tsx src/locales.test.ts`
Expected: PASS.

If the "swallows Escape" test still fails, the cause is React's delegation target rather than the handler: attach the listener to the dialog element itself (`role="dialog"`) as well as the backdrop, and keep `stopImmediatePropagation`. Do not change `PreferencesModal` to work around it - a dialog that leaks Escape is the bug.

- [ ] **Step 6: Write the failing panel tests for the reveal row**

In `RecordingsSection.test.tsx`, add:

```tsx
  it("shows the chosen folder as a path chip with a Change control, only in fixed-folder mode", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...settings, placementMode: "SpecificFolder", placementSectionId: "acme",
    });
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
    ]);
    renderSection();

    expect(await screen.findByText("Customers › Acme Corp")).toBeTruthy();
    // The picker is a dialog now - nothing of it is on the panel until asked for.
    expect(screen.queryByLabelText("Filter folders")).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: /always ungrouped/i }));
    expect(screen.queryByRole("button", { name: /change/i })).toBeNull();
  });

  it("opens the picker from Change, and applies the choice when it closes", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    fireEvent.click(screen.getByRole("button", { name: /change/i }));

    expect(screen.getByRole("dialog", { name: "Choose a folder" })).toBeTruthy();
    fireEvent.click(screen.getByLabelText("Select Projects"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    expect(screen.queryByRole("dialog", { name: "Choose a folder" })).toBeNull();
    expect(screen.getByText("Projects")).toBeTruthy();
    fireEvent.click(saveButton());
    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({
        placementMode: "SpecificFolder", placementSectionId: "sec-1",
        calendarAutoStopEnabled: false, calendarAutoStopAfterMinutes: 3, calendarSilenceStopSeconds: 30,
      }),
    );
  });

  it("returns focus to Change when the picker closes", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    const change = screen.getByRole("button", { name: /change/i });
    fireEvent.click(change);
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(document.activeElement).toBe(change);
  });
```

**Delete** these now-obsolete tests from the file, replaced by the ones above and by `FolderPickerModal.test.tsx`:
- `labels the folder picker for assistive tech, associated with the visible 'Folder' heading` - the dialog names itself, so there is no `role="group"` wrapper left to name.
- `is keyboard operable: Tab alone reaches a folder row, and Enter chooses it` - the tab chain now runs through the dialog, which its own test covers.
- `shows the previously saved folder's full path when it is nested too deep to appear at the picker's root` - the path chip shows it unconditionally now, which the first new test above asserts.

Keep `saves a specific-folder placement...`, `sets the fixed folder to Ungrouped...`, `clears the fixed folder when a non-specific mode is chosen` and `marks the previously saved folder as selected in the picker (round-trip)`, opening the dialog first where they used to interact with the inline picker.

- [ ] **Step 7: Run them and verify they fail**

Run: `npm test -- src/components/RecordingsSection.test.tsx`
Expected: FAIL - `Unable to find an element with the text: Customers › Acme Corp`.

- [ ] **Step 8: Add the reveal row and the dialog to the panel**

Add the `en` `account` key (and mirrors):
- `en`: `"placementChange": "Change..."`
- `de`: `"placementChange": "Ändern..."`
- `es`: `"placementChange": "Cambiar..."`
- `fr`: `"placementChange": "Modifier..."`

Remove `"placementFolder"` from all four - the dialog names itself now.

In `RecordingsSection.tsx`, swap the `FolderPicker` import for the modal and add the tree helper:
```tsx
import FolderPickerModal from "./FolderPickerModal";
import { orderedSections } from "../lib/sectionTree";
```

Add the state and the derived path near the other state:
```tsx
  const [pickerOpen, setPickerOpen] = useState(false);
  const changeRef = useRef<HTMLButtonElement>(null);
```
(add `useRef` to the `react` import).

Delete the standalone `{placementMode === "SpecificFolder" && (...)}` block that sits after the fieldset. The row now lives **inside** the `SpecificFolder` card - as a sibling of the card's `<div className="flex items-start gap-3">`, still within the same `<label>`, so it renders below the title and description rather than beside them. Add this immediately after that flex row's closing `</div>` in the `.map` from Task 3:

```tsx
            {card.mode === "SpecificFolder" && placementMode === "SpecificFolder" && (
              // Indented to line up under the card title rather than the radio.
              <div className="mt-2.5 flex flex-wrap items-center gap-2 pl-8">
                <span className="inline-flex items-center gap-2 rounded-md border px-2.5 py-1.5 text-[13px] dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100">
                  <FolderIcon size={14} />
                  <span className="truncate">{chosenPath}</span>
                </span>
                <button
                  type="button"
                  ref={changeRef}
                  // Inside a <label>: without this the click also toggles the radio, and in Firefox it
                  // would re-focus the input instead of opening the dialog.
                  onClick={(e) => {
                    e.preventDefault();
                    setPickerOpen(true);
                  }}
                  className="rounded-md border px-2.5 py-1.5 text-[13px] hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                >
                  {t("placementChange")}
                </button>
              </div>
            )}
```

Add `FolderIcon` to the `./icons` import, and compute the path beside the other derived values:
```tsx
  // The panel shows the chosen folder's full path unconditionally - the old inline picker could only show
  // a folder that happened to be at its current drill level, so a deeply nested choice looked unset.
  const chosenPath =
    placementSectionId === null
      ? tWorkspace("ungrouped")
      : (orderedSections(sections).find((o) => o.section.id === placementSectionId)?.label ??
        tWorkspace("ungrouped"));
```
with a second translator for the `workspace` namespace:
```tsx
  const { t: tWorkspace } = useTranslation("workspace");
```

Render the dialog at the end of the component's returned fragment:
```tsx
      {pickerOpen && (
        <FolderPickerModal
          sections={sections}
          selectedId={placementSectionId}
          onSelect={setPlacementSectionId}
          onClose={() => {
            setPickerOpen(false);
            changeRef.current?.focus();
          }}
        />
      )}
```

- [ ] **Step 9: Run the whole suite**

Run: `npm test`
Expected: PASS, including `MoveToSectionModal.test.tsx` and `FolderPicker.test.tsx` - neither component was touched, and a failure there means something leaked.

- [ ] **Step 10: Typecheck**

Run: `npm run build`
Expected: exit 0.

- [ ] **Step 11: Commit**

```bash
git add apps/web/src/components/FolderPickerModal.tsx apps/web/src/components/FolderPickerModal.test.tsx apps/web/src/components/RecordingsSection.tsx apps/web/src/components/RecordingsSection.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json apps/web/src/locales/en/account.json apps/web/src/locales/de/account.json apps/web/src/locales/es/account.json apps/web/src/locales/fr/account.json
git commit -m "feat(web): choose the fixed recording folder in its own dialog"
```

---

### Task 6: Browser verification, docs and the release

The `overflow-x` invariant cannot be asserted in jsdom - it computes no geometry - so it is verified in the running app and reported as such. Everything else in this task is the repo's release checklist.

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `docs/features.md` (line 33)
- Modify: `apps/web/src/content/help/en/recording-audio.md` (lines 94-96), `apps/web/src/content/help/en/organizing-folders.md` (lines 18-20)

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: the PR.

- [ ] **Step 1: Verify the panel in a browser**

Start the dev server with `preview_start` (never `npm run dev` through Bash) using `.claude/launch.json`, open Preferences -> Recordings, and check:
1. Select the third card. The content pane must **not** grow a horizontal scrollbar. Confirm with `javascript_tool`:
   ```js
   const p = [...document.querySelectorAll('div')].find(d => d.scrollHeight > d.clientHeight && d.className.includes('overflow-y-auto'));
   [p.scrollWidth, p.clientWidth]
   ```
   Expected: the two numbers are equal. If `scrollWidth` is larger, the selection styling is painting outside the card - fix the card, not the pane.
2. Turn the switch on at the modal's minimum width. The two sentence rows must wrap rather than squash the 60px inputs.
3. `resize_window` to dark and light and screenshot both.

Report the measured numbers, not an impression.

- [ ] **Step 2: Bump the version in all five places**

`0.203.0` -> `0.204.0` (functional enhancement: minor +1, build reset to 0).

- `version.json`: `{ "version": "0.204.0" }`
- `apps/web/package.json`: `"version": "0.204.0"`
- `apps/desktop/package.json`: `"version": "0.204.0"`
- `src/Diariz.Api/Diariz.Api.csproj`: `<Version>0.204.0</Version>`
- `integrations/n8n-nodes-diariz/package.json`: `"version": "0.204.0"`

- [ ] **Step 3: Add the release entry**

Insert at the top of `RELEASES` in `apps/web/src/lib/releases.ts`. Leave `pr` out for now - Step 8 fills it in with the real number, because guessing "last + 1" is wrong whenever a Dependabot PR or an issue took the number.

```ts
  {
    version: "0.204.0",
    date: "2026-08-11",
    headline: "Preferences -> Recordings, rebuilt",
    summary:
      "The Recordings tab in Preferences now says what each choice actually does. The three placement options are cards carrying a one-line consequence, so you can tell them apart without trying them. The calendar auto-stop settings are one switch: turn it on and the two durations appear as a sentence you can read, with a worked example in your own numbers showing exactly when a 10:00-11:00 meeting would stop recording; turn it off and they are gone rather than sitting there greyed out. Save has moved to the modal footer next to Close, with the panel telling you whether you have unsaved changes. Choosing a fixed folder now opens a proper dialog, and the folder you picked is shown as its full path whether or not it is nested deeply.",
    added: [
      "A worked example under the calendar auto-stop settings, recomputed as you type.",
      "An unsaved-changes indicator in the Preferences footer.",
      "A Choose a folder dialog for the fixed-folder setting, with the chosen folder shown as a full path.",
    ],
    changed: [
      "The three recording-placement options are cards that say what each one does.",
      "Calendar auto-stop is a single switch; its two durations appear only when it is on, instead of sitting disabled.",
      "Save for the Recordings tab moved from the panel body to the modal footer.",
      "The Preferences header names the panel you are on.",
    ],
  },
```

- [ ] **Step 4: Run the version gates**

Run: `npm test -- src/lib/releases.test.ts src/lib/versionMirrors.test.ts`
Expected: PASS. `versionMirrors.test.ts` exists precisely because the n8n node once sat at `0.1.0` for ~70 releases and a published npm version cannot be corrected.

- [ ] **Step 5: Fix the stale doc references**

`docs/features.md` line 33 currently reads:
```
Under **Preferences → Recordings → Recording from a Calendar Event**
```
That heading no longer exists. Replace with:
```
Under **Preferences → Recordings**, the switch **Let a calendar meeting end its own recording**
```

`apps/web/src/content/help/en/recording-audio.md` lines 94-96 currently read:
```
By default a new recording is filed into the folder you currently have open. You can change this in
**Settings -> Recordings** to always use a specific folder, or to leave new recordings ungrouped. If a
shared room is open when you record, the meeting is filed into that room as well.
```
Replace with (ASCII only, no fancy dashes):
```
By default a new recording is filed into the folder you currently have open. You can change this in
**Preferences -> Recordings**, where each choice is a card saying what it does: keep the open folder,
send everything to Ungrouped, or pick one fixed folder. Changes there are saved with the **Save changes**
button in the footer of the Preferences window. If a shared room is open when you record, the meeting is
filed into that room as well.
```

`apps/web/src/content/help/en/organizing-folders.md` lines 18-20 currently read:
```
the top to pick it. The same picker is used for choosing where a new recording is filed, in
**Settings -> Recordings**. If you choose **Use the currently selected folder** there, a new recording
```
Replace with:
```
the top to pick it. The same picker is used for choosing where a new recording is filed: in
**Preferences -> Recordings**, pick **One fixed folder** and press **Change...** to open it. If you
choose **The folder I'm looking at** instead, a new recording
```

- [ ] **Step 6: Check the remaining checklist items and record the answer**

- `CAPABILITIES` in `releases.ts`: this redesigns an existing capability rather than adding one. Read the **Capture** and **Record a calendar meeting** rows; if neither states anything now false, leave them. Say which you checked in the PR body.
- README **Features** table and `docs/features.md`: the `Organise & merge` row says "choose where a new recording is filed (Ungrouped, the open folder, or a specific folder)" - still true, no edit. Confirm rather than assume.
- `docs/Overall_Synopsis_of_Platform.md` and `docs/Data_Schema.md`: **no edit**. No architecture, contract, endpoint, dependency or schema change.

- [ ] **Step 7: Run everything and verify green**

Run: `npm test`
Expected: PASS with no errors or warnings - a passing run in this repo is pristine.

Then, from the repo root, confirm nothing outside `apps/web` and the docs changed:

```bash
git status --short
```
Expected: only the files listed in this plan.

- [ ] **Step 8: Push and open the PR, then backfill the PR number**

```bash
git push -u origin feat/recordings-preferences-redesign
```

Open the PR with `gh pr create`. The body must state:
- what changed and why;
- the three deliberate deviations from the handoff (Folder not Section; the picker body keeps `FolderPicker`'s semantics and styling; the close control is the codebase's `✕` convention rather than a new icon component);
- the browser-measured `scrollWidth` / `clientWidth` result from Step 1;
- **Deployment surface: web only - server redeploy, no desktop release.** Nothing under `apps/desktop/src/**`, `apps/desktop/build/**`, `electron-builder.config.js` or desktop dependencies is touched; the lockstep `apps/desktop/package.json` bump does not by itself require one.
- that `docs/Overall_Synopsis_of_Platform.md` and `docs/Data_Schema.md` were checked and need no change.

Then set the real number in `releases.ts` and push again:

```bash
git add apps/web/src/lib/releases.ts
git commit -m "chore(release): confirm the PR number for 0.204.0"
git push
```

- [ ] **Step 9: Commit the doc and version work**

Run before Step 8's push:

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts docs/features.md apps/web/src/content/help/en/recording-audio.md apps/web/src/content/help/en/organizing-folders.md
git commit -m "chore(release): 0.204.0 - Recordings preferences panel redesign"
```

---

## Notes for the implementer

- **`dotnet` is not involved.** This PR touches no .NET file, so there is no need to build `Diariz.slnx` or run the integration tests.
- **If a test passes the first time you run it, you have not written a test.** Break the implementation deliberately and confirm the failure message before moving on. Tautological tests are the dominant defect class in this repo.
- **Any edit made after the last green run is unverified.** A comment-only change in a JSX return has shipped a blank page past a full green suite here before. Re-run `npm test` after the final edit, however trivial.
- **The `✕` character** in `FolderPickerModal.tsx` is deliberate and matches `ManageUsersModal.tsx`. It is not a dash and is unaffected by the no-em-dash rule.
