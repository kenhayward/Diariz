# Scheduled recording auto-stop (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this
> plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let a user schedule the current recording to auto-stop at a chosen time (or after 15/30/60 min),
which ends the recording and starts the normal transcription pipeline - no manual Stop needed. Resolves
GitHub issue #294.

**Architecture:** Frontend-only. The stop path already runs the pipeline (`Recorder.stop()` -> `MediaRecorder`
`onstop` -> `upload()` -> transcription), so a scheduled stop is just "call `stop()` at time T". The schedule
math goes in a **pure, unit-tested** module (`recorderSchedule.ts`, matching `recorderTiming.ts`), and the
Recorder's existing 250 ms ticker checks it and calls `stop()`. No API / schema / worker changes.

**Tech Stack:** React 19 + TS; vitest/RTL. **Release:** Minor bump `0.132.3` -> `0.133.0` (functional
enhancement). **Deployment:** server redeploy (web). No desktop release (the Electron tray shares the same
`stop()`, so tray-driven recordings auto-stop too - no shell change).

## Locked behaviour (agreed in the issue thread)
- Control: an **Auto-stop** `<select>` in the recorder row, right of the Upload button:
  `Off` (default) · `In 15 minutes` · `In 30 minutes` · `In 1 hour` · `At a set time…`. Choosing "At a set
  time…" reveals an `<input type="time">`.
- **Relative** choices resolve to `anchor + N minutes`, where `anchor` = **record-start** if the choice was
  set before recording, or **now** if changed while recording (so it's always "record for N minutes", never a
  target that has already elapsed).
- **"At a set time"**: today at the entered local `HH:MM`; only valid when **in the future**. Fires at that
  wall-clock time.
- Auto-stop only fires **while recording**; a paused recording still auto-stops at its target (it's a
  scheduled time). A past/blank target never stops (so it can't instant-stop on Record).
- The chosen option persists to localStorage (like the other recorder settings); the resolved target is
  cleared on stop.

## Files
- Create: `apps/web/src/lib/recorderSchedule.ts` (pure) + `recorderSchedule.test.ts`.
- Modify: `apps/web/src/components/Recorder.tsx` (state, wiring, UI) + `Recorder.test.tsx` (one auto-stop test).
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json` (new keys).
- Docs: README Features row + `docs/features.md` (Capture bullet) + `releases.ts` CAPABILITIES (Capture row) +
  the `RELEASES[0]` entry; version files. (No synopsis/schema change.)

---

## Task 1: Pure schedule module (TDD)

**Files:** Create `apps/web/src/lib/recorderSchedule.ts`, `apps/web/src/lib/recorderSchedule.test.ts`.

- [ ] **Step 1: Failing tests**

Create `apps/web/src/lib/recorderSchedule.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { parseTimeToday, resolveStopAt, shouldStop, RELATIVE_MINUTES } from "./recorderSchedule";

// A fixed "now": 2026-07-13 14:00:00 local.
const now = new Date(2026, 6, 13, 14, 0, 0).getTime();

describe("parseTimeToday", () => {
  it("parses HH:MM into today's epoch (local)", () => {
    expect(parseTimeToday("15:30", now)).toBe(new Date(2026, 6, 13, 15, 30, 0).getTime());
  });
  it("returns null for blank or malformed input", () => {
    expect(parseTimeToday("", now)).toBeNull();
    expect(parseTimeToday("25:99", now)).toBeNull();
    expect(parseTimeToday("nope", now)).toBeNull();
  });
});

describe("resolveStopAt", () => {
  it("off -> null", () => {
    expect(resolveStopAt("off", "", now, now)).toBeNull();
  });
  it("relative -> anchor + N minutes", () => {
    expect(resolveStopAt("in15", "", now, now)).toBe(now + 15 * 60_000);
    expect(resolveStopAt("in30", "", now, now)).toBe(now + 30 * 60_000);
    expect(resolveStopAt("in60", "", now, now)).toBe(now + 60 * 60_000);
    // Uses the passed anchor, not `now`, so a pre-recording choice anchors to record-start.
    expect(resolveStopAt("in15", "", now + 5 * 60_000, now)).toBe(now + 20 * 60_000);
  });
  it("at -> today's HH:MM only when in the future", () => {
    expect(resolveStopAt("at", "15:00", now, now)).toBe(new Date(2026, 6, 13, 15, 0, 0).getTime());
    expect(resolveStopAt("at", "13:00", now, now)).toBeNull(); // past
    expect(resolveStopAt("at", "", now, now)).toBeNull(); // blank
  });
});

describe("shouldStop", () => {
  it("true only once now has reached a non-null target", () => {
    expect(shouldStop(now + 1000, now)).toBe(false);
    expect(shouldStop(now, now)).toBe(true);
    expect(shouldStop(now - 1, now)).toBe(true);
    expect(shouldStop(null, now)).toBe(false);
  });
});

describe("RELATIVE_MINUTES", () => {
  it("maps the relative choices to minutes", () => {
    expect(RELATIVE_MINUTES).toEqual({ in15: 15, in30: 30, in60: 60 });
  });
});
```

Run: `cd apps/web && npx vitest run src/lib/recorderSchedule.test.ts` -> FAIL (module missing).

- [ ] **Step 2: Implement**

Create `apps/web/src/lib/recorderSchedule.ts`:

```ts
/// Pure schedule math for the recorder's auto-stop control (kept out of Recorder.tsx so it can be unit-tested
/// without MediaRecorder/timers, like recorderTiming.ts). The Recorder holds the chosen option; this resolves
/// it to an absolute stop time and answers "stop now?".

export type AutoStopChoice = "off" | "in15" | "in30" | "in60" | "at";

export const RELATIVE_MINUTES: Record<"in15" | "in30" | "in60", number> = { in15: 15, in30: 30, in60: 60 };

/// Parse a local "HH:MM" into today's epoch ms, or null when blank/malformed. Does not roll to tomorrow - the
/// caller (resolveStopAt) requires the result to be in the future.
export function parseTimeToday(input: string, now: number): number | null {
  const m = /^(\d{1,2}):(\d{2})$/.exec(input.trim());
  if (!m) return null;
  const h = Number(m[1]);
  const min = Number(m[2]);
  if (h > 23 || min > 59) return null;
  const d = new Date(now);
  d.setHours(h, min, 0, 0);
  return d.getTime();
}

/// The absolute stop target for a choice, or null when off / unresolved / not in the future.
/// `anchorMs` is the base for relative choices (record-start when set before recording, else the moment of
/// selection). Ignored for "at".
export function resolveStopAt(
  choice: AutoStopChoice,
  timeInput: string,
  anchorMs: number,
  now: number,
): number | null {
  if (choice === "off") return null;
  if (choice === "at") {
    const at = parseTimeToday(timeInput, now);
    return at != null && at > now ? at : null;
  }
  return anchorMs + RELATIVE_MINUTES[choice] * 60_000;
}

/// Whether recording should auto-stop now.
export function shouldStop(stopAt: number | null, now: number): boolean {
  return stopAt != null && now >= stopAt;
}
```

Run: `cd apps/web && npx vitest run src/lib/recorderSchedule.test.ts` -> PASS.

- [ ] **Step 3: Commit**
```bash
git add apps/web/src/lib/recorderSchedule.ts apps/web/src/lib/recorderSchedule.test.ts
git commit -m "feat(recorder): pure schedule math for auto-stop"
```

---

## Task 2: i18n keys

**Files:** `apps/web/src/locales/{en,de,es,fr}/workspace.json`

- [ ] **Step 1: Add keys** (anchor near the existing `rec*` recorder keys). Values:

| key | en | de | es | fr |
| --- | --- | --- | --- | --- |
| `autoStopLabel` | Auto-stop | Automatisch stoppen | Detención automática | Arrêt automatique |
| `autoStopOff` | No auto-stop | Kein automatisches Stoppen | Sin detención automática | Pas d'arrêt automatique |
| `autoStopIn15` | Stop in 15 minutes | In 15 Minuten stoppen | Detener en 15 minutos | Arrêter dans 15 minutes |
| `autoStopIn30` | Stop in 30 minutes | In 30 Minuten stoppen | Detener en 30 minutos | Arrêter dans 30 minutes |
| `autoStopIn60` | Stop in 1 hour | In 1 Stunde stoppen | Detener en 1 hora | Arrêter dans 1 heure |
| `autoStopAt` | Stop at a set time… | Zu fester Zeit stoppen… | Detener a una hora fija… | Arrêter à une heure définie… |
| `autoStopAtAria` | Auto-stop time | Uhrzeit für automatisches Stoppen | Hora de detención automática | Heure d'arrêt automatique |
| `autoStopScheduled` | stops at {{time}} | stoppt um {{time}} | se detiene a las {{time}} | s'arrête à {{time}} |

No em/en dashes; the `…` ellipsis is fine (it's not a dash).

- [ ] **Step 2: Verify JSON parses**
```bash
node -e "for(const l of ['en','de','es','fr'])require('./apps/web/src/locales/'+l+'/workspace.json')"
```
- [ ] **Step 3: Commit**
```bash
git add apps/web/src/locales
git commit -m "i18n(recorder): auto-stop strings"
```

---

## Task 3: Wire the control into the Recorder (TDD)

**Files:** `apps/web/src/components/Recorder.tsx`, `apps/web/src/components/Recorder.test.tsx`

- [ ] **Step 1: Failing Recorder test (auto-stop fires)**

Add to `Recorder.test.tsx` (it already stubs `MediaRecorder`, `getStream`, `api.upload`). Use fake timers so a
relative target elapses instantly. Pattern:

```ts
it("auto-stops the recording and uploads when the scheduled time is reached", async () => {
  vi.useFakeTimers();
  try {
    (getStream as Mock).mockResolvedValue(fakeSession);
    (api.upload as Mock).mockResolvedValue({ id: "r1" });
    render(<Recorder onUploaded={() => {}} />);

    // Choose "in 15 minutes", then start recording.
    fireEvent.change(screen.getByLabelText(/auto-stop/i), { target: { value: "in15" } });
    fireEvent.click(screen.getByLabelText(/record/i));
    // Flush start()'s awaited getStream promise under fake timers.
    await vi.runOnlyPendingTimersAsync();

    // Nothing yet before the 15-minute mark.
    vi.advanceTimersByTime(14 * 60_000);
    expect(api.upload).not.toHaveBeenCalled();

    // Cross the mark: the 250 ms ticker sees shouldStop() and stops -> onstop -> upload().
    await vi.advanceTimersByTimeAsync(60_500);
    expect(api.upload).toHaveBeenCalledTimes(1);
  } finally {
    vi.useRealTimers();
  }
});
```
(If the existing suite's async flushing differs, mirror its `waitFor`/`act` usage - the key assertions are:
before the mark `upload` is not called, after it `upload` is called once. Adjust the flush calls so the
`getStream` promise resolves under fake timers.)

Run: `cd apps/web && npx vitest run src/components/Recorder.test.tsx` -> the new test FAILS.

- [ ] **Step 2: Add state + resolution + ticker check to `Recorder.tsx`**

- Imports: `import * as schedule from "../lib/recorderSchedule";` and
  `import type { AutoStopChoice } from "../lib/recorderSchedule";`.
- Constants + persistence (near `SOURCE_KEY`): `const AUTOSTOP_KEY = "diariz.recorder.autoStop";`. Load the
  saved choice + time on mount into state (mirror `loadSavedSystemAudio` style; default `{ choice: "off",
  time: "" }`).
- State:
  ```tsx
  const [autoStopChoice, setAutoStopChoice] = useState<AutoStopChoice>("off");
  const [autoStopTime, setAutoStopTime] = useState(""); // HH:MM for the "at" option
  const scheduledStopRef = useRef<number | null>(null);   // resolved absolute target, read by the ticker
  const [scheduledStopAt, setScheduledStopAt] = useState<number | null>(null); // mirror for display
  ```
- A resolver that recomputes the target and stores it in both the ref and state:
  ```tsx
  const applySchedule = useCallback((choice: AutoStopChoice, time: string, anchorMs: number) => {
    const at = schedule.resolveStopAt(choice, time, anchorMs, Date.now());
    scheduledStopRef.current = at;
    setScheduledStopAt(at);
  }, []);
  ```
- On change handlers (persist + re-resolve; anchor = now when already recording, else it is resolved at
  Record time):
  ```tsx
  function onAutoStopChoice(choice: AutoStopChoice) {
    setAutoStopChoice(choice);
    persistAutoStop(choice, autoStopTime);
    applySchedule(choice, autoStopTime, Date.now());
  }
  function onAutoStopTime(time: string) {
    setAutoStopTime(time);
    persistAutoStop(autoStopChoice, time);
    applySchedule(autoStopChoice, time, Date.now());
  }
  ```
  (`persistAutoStop` writes `{choice, time}` JSON to `AUTOSTOP_KEY`, try/catch like the others.)
- In `start()`, after `timingRef.current = timing.start(Date.now());` re-anchor a relative choice to
  record-start so "in N minutes" means N minutes of recording:
  ```tsx
      applySchedule(autoStopChoice, autoStopTime, Date.now());
  ```
- In the ticker (`startTicker`), also check the schedule and stop:
  ```tsx
  timerRef.current = window.setInterval(() => {
    const now = Date.now();
    setElapsed(timing.elapsedMs(timingRef.current, now));
    if (schedule.shouldStop(scheduledStopRef.current, now)) stop();
  }, 250);
  ```
  (`stop()` is defined below `startTicker`; it's a stable function reference in the component scope - a plain
  call is fine. If TS complains about use-before-declaration for the interval body, note the body runs later at
  runtime; keep `stop` as a `function` declaration so it's hoisted.)
- In `stop()`, clear the resolved target so a finished schedule can't re-fire and the display clears:
  ```tsx
      scheduledStopRef.current = null;
      setScheduledStopAt(null);
  ```

- [ ] **Step 3: Add the UI** (in the control row, right after the Upload button `<input>` at ~L872)

```tsx
        {/* Schedule the current recording to auto-stop (then the normal upload+transcription runs). */}
        <select
          value={autoStopChoice}
          onChange={(e) => onAutoStopChoice(e.target.value as AutoStopChoice)}
          disabled={busy || !canRecord}
          aria-label={t("autoStopLabel")}
          title={t("autoStopLabel")}
          className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        >
          <option value="off">{t("autoStopOff")}</option>
          <option value="in15">{t("autoStopIn15")}</option>
          <option value="in30">{t("autoStopIn30")}</option>
          <option value="in60">{t("autoStopIn60")}</option>
          <option value="at">{t("autoStopAt")}</option>
        </select>
        {autoStopChoice === "at" && (
          <input
            type="time"
            value={autoStopTime}
            onChange={(e) => onAutoStopTime(e.target.value)}
            aria-label={t("autoStopAtAria")}
            className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
        )}
```

And show the scheduled stop next to the live timer while recording (inside the `{recording && (...)}` block,
after the `mmss` span):
```tsx
            {scheduledStopAt != null && (
              <span className="font-mono text-xs text-gray-500 dark:text-gray-400">
                {t("autoStopScheduled", {
                  time: new Date(scheduledStopAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }),
                })}
              </span>
            )}
```

- [ ] **Step 4: Green + typecheck + full suite**
```bash
cd apps/web && npx vitest run src/components/Recorder.test.tsx   # PASS
cd apps/web && npm run build                                     # tsc + vite
cd apps/web && npm test                                          # whole suite green, pristine
```

- [ ] **Step 5: Commit**
```bash
git add apps/web/src/components/Recorder.tsx apps/web/src/components/Recorder.test.tsx
git commit -m "feat(recorder): scheduled auto-stop control (issue #294)"
```

---

## Task 4: Docs + version bump

**Files:** `README.md`, `docs/features.md`, `apps/web/src/lib/releases.ts`, version files.

- [ ] **Step 1: Docs**
- README Features **Capture** row + `docs/features.md` Capture bullet: mention "schedule a recording to
  auto-stop at a set time or after 15/30/60 minutes".
- `releases.ts` `CAPABILITIES` **Capture** row: append the same (concise). No em/en dashes.

- [ ] **Step 2: Failing release guard -> bump**
Set `version.json` to `0.133.0`: `cd apps/web && npm test -- releases` -> FAIL. Then set the three mirrors
(`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`) to `0.133.0` and
prepend `RELEASES[0]`:
```ts
  {
    version: "0.133.0",
    date: "2026-07-13",
    pr: 0, // set to the real PR number after opening the PR
    headline: "Schedule a recording to stop itself",
    summary:
      "You can now set the recorder to stop on its own - pick \"in 15 / 30 minutes\" or \"in 1 hour\", or a specific clock time, from the new Auto-stop control next to the record button. When the time arrives the recording ends and its transcription starts automatically, so you can start a meeting recording and walk away.",
    added: [
      "Auto-stop: schedule the current recording to end after 15/30/60 minutes or at a set time, which then starts transcription automatically.",
    ],
  },
```

- [ ] **Step 3: Green + build**
```bash
cd apps/web && npm test -- releases   # PASS
cd apps/web && npm run build          # PASS
```
- [ ] **Step 4: Commit**
```bash
git add README.md docs/features.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.133.0 - scheduled recording auto-stop"
```

---

## Finish
- [ ] `cd apps/web && npm test` + `npm run build` green and pristine.
- [ ] Live browser check (coordinator): set Auto-stop to "At a set time…" a minute ahead, record, confirm it
  stops and a recording appears; set "in 15 minutes" and confirm the "stops at HH:MM" hint shows.
- [ ] Use **superpowers:finishing-a-development-branch**: push `feat/scheduled-stop`, open a PR. Deployment =
  **server redeploy (web)**; no desktop release; no migration. State it fixes #294.
- [ ] Set `RELEASES[0].pr`.

## Self-review checklist
- Spec coverage: pure schedule math (T1) / i18n (T2) / control + ticker auto-stop + display + persistence (T3)
  / docs + Minor bump (T4). ✓
- No placeholders: full code for the module, its tests, the Recorder wiring, the UI, and the release entry. ✓
- Consistency: `AutoStopChoice` values (`off/in15/in30/in60/at`) identical across module, tests, state, and
  `<option>`s; `resolveStopAt` anchor semantics documented (record-start pre-recording via `start()`, now on
  change). ✓
- Footgun guard: a null/past target never stops (shouldStop null-safe; "at" future-only); target cleared in
  `stop()` so it can't re-fire. ✓
