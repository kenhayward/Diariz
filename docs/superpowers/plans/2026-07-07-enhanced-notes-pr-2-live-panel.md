# Enhanced Notes - PR 2 (Live notes panel) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** While recording, a notes panel lets the user commit lines stamped with the current *recorded* time (pause-aware); lines survive crashes via IndexedDB and are attached to the recording automatically after upload.

**Architecture:** Web-only. `Recorder.tsx` owns the live lines (it owns `timingRef`); a `LiveNotesPanel` (reusing the dumb `NotesSection`) renders while recording. A new `lib/pendingNotes.ts` mirrors the `pendingRecording` pattern in a **separate IndexedDB database** (`diariz-notes` - the existing `diariz` DB is at version 1 and adding a store would force a version bump in two modules; a separate DB is zero-interference, still keyed by userId). After `api.upload` succeeds, the lines bulk-POST to `/api/recordings/{id}/notes` (PR 1's endpoint); on attach failure they stay durable with the recording id and a retry banner appears.

**Tech Stack:** React 19 + TS, Vitest (jsdom; IndexedDB mocked like `pendingRecording`).

**Lifecycle rules (locked):**
- Lines mirror to IndexedDB on every change (`recordingId: null` while recording).
- `upload()` success → attach lines → clear mirror. Attach failure → persist with `recordingId` set → retry banner.
- `uploadPending()` (recovered audio) → after upload, attach any stored `recordingId: null` lines to the new recording (same success/failure handling).
- `discardPending()` also clears stored `recordingId: null` lines (notes about discarded audio die with it).
- Starting a **new** recording clears any stale `recordingId: null` lines (orphans from a crash mid-recording whose audio never reached Stop - the blob is only stashed at Stop, so there is nothing to attach them to).
- Panel auto-opens when recording starts unless the user closed it before (localStorage `diariz.recorder.notesOpen`, default open); a Notes toggle shows while recording.

---

## File map

**Create**
- `apps/web/src/lib/pendingNotes.ts` (+ used via mocks in tests; module itself exercised through Recorder tests)
- `apps/web/src/components/LiveNotesPanel.tsx` (+ `LiveNotesPanel.test.tsx`)

**Modify**
- `apps/web/src/components/Recorder.tsx` (lines state, stamps, mirror, attach, retry banner, toggle)
- `apps/web/src/components/Recorder.test.tsx` (mock pendingNotes + api.createNotes; new cases)
- `apps/web/src/locales/{en,es,fr,de}/workspace.json`
- `releases.ts` (`RELEASES[0]` + CAPABILITIES sentence), version mirrors → **0.104.0**
- `docs/Overall_Synopsis_of_Platform.md` (the notes paragraph gains the live-panel sentence)

---

## Task 1: `pendingNotes` durability module

**Files:** Create `apps/web/src/lib/pendingNotes.ts`.

- [ ] **Step 1: Implement** (mirrors `pendingRecording.ts`; separate DB name; best-effort no-ops):

```typescript
/// Durable stash for live-recording note lines, so a crash/session lapse never loses them. Mirrors the
/// pendingRecording pattern but in its own database (`diariz-notes`) - adding a store to the existing
/// `diariz` DB would force a version bump across modules. Keyed by user id. `recordingId` is null while
/// the recording is still in progress; it is set when the audio uploaded but the notes attach failed, so
/// the retry banner knows where the lines belong. All operations degrade to no-ops without IndexedDB.

export interface PendingNoteLine {
  text: string;
  capturedAtMs: number | null;
}

export interface PendingNotes {
  userId: string;
  lines: PendingNoteLine[];
  /// Null while recording; the created recording's id once audio uploaded but the notes attach failed.
  recordingId: string | null;
  updatedAt: number;
}

const DB_NAME = "diariz-notes";
const STORE = "pending-notes";

function openDb(): Promise<IDBDatabase> | null {
  if (typeof indexedDB === "undefined") return null;
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => {
      if (!req.result.objectStoreNames.contains(STORE)) {
        req.result.createObjectStore(STORE, { keyPath: "userId" });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function withStore<T>(
  mode: IDBTransactionMode,
  fn: (store: IDBObjectStore) => IDBRequest,
): Promise<T | null> {
  const dbp = openDb();
  if (!dbp) return null;
  try {
    const db = await dbp;
    return await new Promise<T | null>((resolve, reject) => {
      const tx = db.transaction(STORE, mode);
      const req = fn(tx.objectStore(STORE));
      req.onsuccess = () => resolve((req.result as T) ?? null);
      req.onerror = () => reject(req.error);
    });
  } catch {
    return null; // best-effort: never let a storage hiccup break note-taking
  }
}

export async function savePendingNotes(notes: PendingNotes): Promise<void> {
  await withStore("readwrite", (s) => s.put(notes));
}

export async function loadPendingNotes(userId: string): Promise<PendingNotes | null> {
  return withStore<PendingNotes>("readonly", (s) => s.get(userId));
}

export async function clearPendingNotes(userId: string): Promise<void> {
  await withStore("readwrite", (s) => s.delete(userId));
}
```

- [ ] **Step 2: Typecheck** (`cd apps/web && npm run build`) then commit:

```bash
git add apps/web/src/lib/pendingNotes.ts
git commit -m "feat(notes): pendingNotes IndexedDB stash (crash-durable live lines)"
```

(No standalone unit test - like `pendingRecording`, the module is a thin IndexedDB wrapper mocked in
component tests; jsdom has no IndexedDB so `openDb` returns null and everything no-ops.)

---

## Task 2: `LiveNotesPanel` component (TDD)

A floating panel shown while recording: header ("Notes while recording" + close ✕), a `NotesSection` over
**local** lines (fake ids), no jump handler. Dumb: lines + callbacks come from `Recorder`.

**Files:** Create `apps/web/src/components/LiveNotesPanel.tsx` + `LiveNotesPanel.test.tsx`; i18n keys.

- [ ] **Step 1: i18n keys** (all four `workspace.json`, translate; plain hyphens):
`"liveNotesTitle": "Notes while recording"`, `"liveNotesHint": "Each line is stamped at the moment you press Enter."`,
`"liveNotesToggle": "Notes"`, `"liveNotesClose": "Close notes"`,
`"notesAttachFailed": "Your notes were saved but could not be attached to the recording."`,
`"notesAttachRetry": "Attach notes"`.

- [ ] **Step 2: Failing test** (`LiveNotesPanel.test.tsx`):

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import LiveNotesPanel from "./LiveNotesPanel";

const lines = [{ id: "l1", text: "Comp expectations", capturedAtMs: 61_000, ordinal: 0, createdAt: "" }];

describe("LiveNotesPanel", () => {
  it("renders committed lines with stamps and commits a new one on Enter", () => {
    const onAdd = vi.fn();
    render(<LiveNotesPanel lines={lines} onAdd={onAdd} onEdit={vi.fn()} onDelete={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByText("Comp expectations")).toBeTruthy();
    expect(screen.getByText("1:01")).toBeTruthy();
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "IPO experience" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(onAdd).toHaveBeenCalledWith("IPO experience");
  });

  it("closes via the header button", () => {
    const onClose = vi.fn();
    render(<LiveNotesPanel lines={[]} onAdd={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} onClose={onClose} />);
    fireEvent.click(screen.getByRole("button", { name: /close notes/i }));
    expect(onClose).toHaveBeenCalled();
  });
});
```

- [ ] **Step 3: Run red, implement:**

```tsx
import { useTranslation } from "react-i18next";
import NotesSection from "./NotesSection";
import type { MeetingNote } from "../lib/types";

/// Floating notes panel shown while recording. Lines are local (client-side) until the upload attaches
/// them; the Recorder owns state, stamping, durability, and attach. Renders below the TopBar on the right.
export default function LiveNotesPanel({
  lines, onAdd, onEdit, onDelete, onClose,
}: {
  lines: MeetingNote[];
  onAdd: (text: string) => void;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation("workspace");
  return (
    <div className="fixed right-4 top-14 z-40 w-80 rounded-lg border bg-white p-3 shadow-xl dark:border-gray-700 dark:bg-gray-900">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-sm font-medium text-gray-700 dark:text-gray-200">{t("liveNotesTitle")}</span>
        <button
          type="button"
          aria-label={t("liveNotesClose")}
          onClick={onClose}
          className="rounded px-1 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
        >
          ✕
        </button>
      </div>
      <p className="mb-2 text-xs text-gray-400 dark:text-gray-500">{t("liveNotesHint")}</p>
      <div className="max-h-72 overflow-y-auto">
        <NotesSection notes={lines} onAdd={onAdd} onEdit={onEdit} onDelete={onDelete} />
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Run green** (`npx vitest run src/components/LiveNotesPanel.test.tsx src/locales.test.ts`), commit:

```bash
git add apps/web/src/components/LiveNotesPanel.tsx apps/web/src/components/LiveNotesPanel.test.tsx apps/web/src/locales
git commit -m "feat(notes): LiveNotesPanel + i18n"
```

---

## Task 3: Wire into `Recorder` (TDD)

**Files:** Modify `apps/web/src/components/Recorder.tsx`, `Recorder.test.tsx`.

- [ ] **Step 1: Extend the test file's mocks** - add to the existing `vi.mock("../lib/api")` block:
`createNotes: vi.fn().mockResolvedValue([])`; ensure `upload` resolves `{ id: "rec-new" }`. Add a new mock:

```tsx
vi.mock("../lib/pendingNotes", () => ({
  savePendingNotes: vi.fn().mockResolvedValue(undefined),
  loadPendingNotes: vi.fn().mockResolvedValue(null),
  clearPendingNotes: vi.fn().mockResolvedValue(undefined),
}));
```

- [ ] **Step 2: Failing tests** (append to `Recorder.test.tsx`; use its existing start/stop helpers - the
FakeMediaRecorder fires `onstop` synchronously from `stop()`):

```tsx
describe("live notes", () => {
  it("shows the notes panel while recording and commits a stamped line", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i); // auto-opened

    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "budget concern" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(await screen.findByText("budget concern")).toBeTruthy();
    // Mirrored to IndexedDB with recordingId null.
    const { savePendingNotes } = await import("../lib/pendingNotes");
    expect(savePendingNotes).toHaveBeenCalledWith(
      expect.objectContaining({ recordingId: null, lines: [expect.objectContaining({ text: "budget concern" })] }),
    );
  });

  it("attaches committed lines to the uploaded recording and clears the stash", async () => {
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "follow up with legal" } });
    fireEvent.keyDown(box, { key: "Enter" });

    fireEvent.click(screen.getByRole("button", { name: /stop/i }));

    const { api } = await import("../lib/api");
    await waitFor(() =>
      expect(api.createNotes).toHaveBeenCalledWith("rec-new", [
        expect.objectContaining({ text: "follow up with legal" }),
      ]),
    );
    const { clearPendingNotes } = await import("../lib/pendingNotes");
    await waitFor(() => expect(clearPendingNotes).toHaveBeenCalled());
  });

  it("keeps lines durable and shows a retry banner when the attach fails", async () => {
    const { api } = await import("../lib/api");
    (api.createNotes as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error("boom"));
    render(<Recorder onUploaded={() => {}} />);
    fireEvent.click(screen.getByRole("button", { name: /record/i }));
    await screen.findByText(/notes while recording/i);
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "x" } });
    fireEvent.keyDown(box, { key: "Enter" });

    fireEvent.click(screen.getByRole("button", { name: /stop/i }));

    expect(await screen.findByText(/could not be attached/i)).toBeTruthy();
    const { savePendingNotes } = await import("../lib/pendingNotes");
    await waitFor(() =>
      expect(savePendingNotes).toHaveBeenCalledWith(expect.objectContaining({ recordingId: "rec-new" })),
    );
    // Retry succeeds and clears the banner.
    fireEvent.click(screen.getByRole("button", { name: /attach notes/i }));
    await waitFor(() => expect(api.createNotes).toHaveBeenCalledTimes(2));
  });
});
```

- [ ] **Step 3: Run red, implement in `Recorder.tsx`:**
  - State/refs: `const [liveLines, setLiveLines] = useState<MeetingNote[]>([]);`
    `const [notesOpen, setNotesOpen] = useState(false);`
    `const [pendingNotesAttach, setPendingNotesAttach] = useState<PendingNotes | null>(null);`
    `const liveLinesRef = useRef<MeetingNote[]>([]);` (read inside `upload()` - state may not have flushed).
  - Local line shape: `MeetingNote` with `id: crypto.randomUUID()`, `ordinal: index`, `createdAt: new Date().toISOString()`.
  - `mirror(lines)` helper: `liveLinesRef.current = lines; setLiveLines(lines);`
    `if (userId) void savePendingNotes({ userId, recordingId: null, updatedAt: Date.now(), lines: lines.map(l => ({ text: l.text, capturedAtMs: l.capturedAtMs })) });`
  - Handlers: `addLiveNote(text)` stamps `timing.elapsedMs(timingRef.current, Date.now())`;
    `editLiveNote(id, text)`; `deleteLiveNote(id)` - each recomputes the array and calls `mirror`.
  - `start()`: clear stale lines (`mirror([])`, and `if (userId) void clearPendingNotes(userId)`), then
    `setNotesOpen(localStorage.getItem(NOTES_OPEN_KEY) !== "false")` (`const NOTES_OPEN_KEY = "diariz.recorder.notesOpen"`).
  - Closing the panel: `setNotesOpen(false); localStorage.setItem(NOTES_OPEN_KEY, "false")`. The **Notes
    toggle** button (visible while `recording`) reopens it and writes `"true"`.
  - `upload()` success path (after `api.upload`, which returns the created summary - capture it:
    `const created = await api.upload(...)`): call new `attachNotes(created.id)`;
  - `attachNotes(recordingId)`: reads `liveLinesRef.current` (or `pendingNotesAttach.lines` on retry); if
    empty → clear + return. Try `api.createNotes(recordingId, lines.map(l => ({ text: l.text, capturedAtMs: l.capturedAtMs })))`;
    success → `clearPendingNotes(userId)`, `mirror([])` (without re-saving), `setPendingNotesAttach(null)`,
    invalidate nothing (notes tab refetches on visit). Failure → `savePendingNotes({ userId, recordingId, lines, updatedAt })`,
    `setPendingNotesAttach(...)` → renders the banner (text `notesAttachFailed` + button `notesAttachRetry`
    that calls `attachNotes(recordingId)` again).
  - `uploadPending()` success path: also `await attachNotes(created.id)` (recovered audio adopts the stored
    `recordingId: null` lines - load them via `loadPendingNotes(userId)` if `liveLinesRef` is empty).
  - `discardPending()`: also `clearPendingNotes(userId)` + `mirror([])`.
  - Mount effect (beside the pendingRecording load): `loadPendingNotes(userId)` → if it has lines with a
    `recordingId`, `setPendingNotesAttach(it)` (banner offers re-attach).
  - Render: `{(recording || paused) && notesOpen && <LiveNotesPanel lines={liveLines} onAdd={addLiveNote} onEdit={editLiveNote} onDelete={deleteLiveNote} onClose={closeNotes} />}`;
    a small `Notes` toggle button beside the timer while recording; the attach-failure banner beside the
    existing pending-recording banner.

- [ ] **Step 4: Run green** - `npx vitest run src/components/Recorder.test.tsx src/components/LiveNotesPanel.test.tsx`
then the full suite + build. Commit:

```bash
git add apps/web/src/components/Recorder.tsx apps/web/src/components/Recorder.test.tsx
git commit -m "feat(notes): live notes while recording - stamped, crash-durable, attached on upload"
```

---

## Task 4: Docs, version, release + verification

- [ ] **Step 1:** `Overall_Synopsis_of_Platform.md` - in the "Meeting notes" section, replace the "Planned
follow-ups: a live notes panel..." sentence with the shipped behaviour (panel auto-opens on record,
Enter-stamped via `recorderTiming`, IndexedDB-durable in `diariz-notes`, attached after upload with retry;
remaining follow-up = the minutes weave).
- [ ] **Step 2:** `CAPABILITIES` - extend the Notes sentence: "...take notes live while recording - each
line stamped at the moment you wrote it".
- [ ] **Step 3:** Version 0.103.0 → **0.104.0** (all mirrors + lockfiles) + `RELEASES[0]` entry (pr number,
headline "Take notes live while you record", added bullets, "Server redeploy (web) only").
- [ ] **Step 4:** Full verification: web build + full vitest; `dotnet build Diariz.slnx` (no API change but
keep the gate); live check - dev server: start a mic recording, panel auto-opens, commit two lines (one
after a pause/resume), stop → notes appear on the new recording's Notes tab with sane stamps; toggle-close
persists across recordings.
- [ ] **Step 5:** Commit docs+version; push; PR.

## Deploy surface

**Server redeploy (web) only** - no API change, no migration, no worker, no desktop release.
