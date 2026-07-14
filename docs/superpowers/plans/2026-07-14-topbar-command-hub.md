# Top Bar "Command Hub" Redesign - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.
>
> **Source design:** `docs/design_handoff_topbar/readme.md` (high-fidelity; all dark values are final) +
> `docs/design_handoff_topbar/Diariz Top Bar.dc.html` (interactive reference - match pixel details, ignore the
> `<sc-if>` prototype mechanism).

**Goal:** Redesign the web app's global top bar into a record-centric **command hub** matching the handoff -
one Record hero that morphs into the live recording state, an "Audio source" chip, and four popovers (audio
source, auto-stop, notes, account) - **reusing all existing recorder/account logic**, made **theme-aware**
(a new light palette pairs with the spec's dark palette) with a **real audio-driven 5-bar level meter**.

**Architecture:** A **presentation-layer refactor**, not new functionality. Every capability already exists
(mic picker, system audio, the four processing flags, auto-stop, notes, tray, usage stats - see the exploration
notes below). We restyle `TopBar.tsx` (the 80px frame), `Recorder.tsx` (the contiguous control cluster: hero →
audio chip → auto-stop → upload → notes + their popovers), and `UserMenu.tsx` (avatar + account popover), and
introduce a `--hub-*` CSS-variable token layer (light in `:root`, dark under `.dark`). The pure logic modules
(`audioSource`, `audioDevices`, `recorderSchedule`, `recorderTiming`, `audioLevel`, `trayRecorder`,
`pendingNotes`, `uploadQueue`, `uploadContext`) are **unchanged**.

**Tech Stack:** React 19 + TS + Vite + Tailwind v4 (CSS-first `@theme`/`@custom-variant dark`). vitest + RTL
(no jest-dom - native assertions only). Existing Feather/Lucide-style inline SVG icons.

---

## What already exists (so we don't rebuild it)

Confirmed in code - the redesign is a reskin of these, keep the logic:
- **Record transport:** `Recorder.tsx` state `recording`/`paused`/`elapsed`, `start/pause/resume/stop/upload`,
  `mmss` formatting, tray reporting.
- **Audio source:** `selection`/`systemAudio`/`devices`, `lib/audioDevices.ts` (`buildSourceOptions`,
  `AudioConstraints`, `DEFAULT_CONSTRAINTS`, `toMediaTrackConstraints`), `lib/audioSource.ts` (get streams).
  Selection persisted `diariz.recorder.source`, system audio `diariz.recorder.systemAudio`, constraints
  `diariz.recorder.audioConstraints`.
- **Four processing flags:** `echoCancellation`/`noiseSuppression`/`autoGainControl`/`mono` already toggle via a
  ⚙ popover and feed `getUserMedia`. Redesign just restyles them as **chips**.
- **Level meter:** `InputLevelMeter.tsx` (single gradient bar, `AnalyserNode` fftSize 256, RMS via
  `lib/audioLevel.ts`, `onSilentChange` → "no sound" hint). Redesign wants **5 bars driven by real audio**.
- **Auto-stop:** `lib/recorderSchedule.ts` + state + a pause-independent 1s watcher. Persisted
  `diariz.recorder.autoStop`.
- **Notes while recording:** `LiveNotesPanel.tsx`/`NotesSection.tsx`, durable via `lib/pendingNotes.ts`,
  attached post-upload via `api.createNotes`. Open-state persisted `diariz.recorder.notesOpen`.
- **Upload:** button + hidden `input[data-testid=upload-input]` in `Recorder.tsx` → `useUpload().uploadFiles`.
- **Account menu:** `UserMenu.tsx` + `useAuth()` (initials/pictureUrl/gating flags) + `useQuery(["user-storage"],
  api.getUserStorage)` → **Storage** + **Transcription time** already rendered. Items: Preferences (always),
  Settings (`isPlatformAdmin`), Manage users (`isAdmin`), Manage platform formulas (`canManageFormulas`),
  Show tour, About, Sign out. `Avatar.tsx` = photo or initials bubble.
- **Recording gate:** `canRecord = can(RoomPermission.CreateRecording)`; Record + Upload disabled without it.
- **Errors/notices** go to the app-wide status bar (`useStatus().setStatus`), not inline - keep that.

**Test contracts to preserve** (or update deliberately): `Recorder.test.tsx` and `UserMenu.test.tsx` assert on
accessible names `record`/`stop`/`pause`/`resume`/`upload`/`system audio`/`auto-stop`/`audio settings`, the
`microphone` combobox, the four constraint checkbox names, `data-testid` `recorder-popover` and `upload-input`,
the `getStream` constraints-object shape, and the storage/transcription lines. Keep these names; update the
structural assertions (select → chip+popover, cog → chips) as the DOM changes.

---

## Design decisions (locked)

1. **Theme-aware.** Introduce `--hub-*` CSS variables; **light values in `:root`, dark overrides under `.dark`**
   (matches the app's class-based dark mode). The dark values are the handoff's final tokens; the light palette
   is defined below (this plan invents it - review it here).
2. **Real audio-driven 5-bar meter.** Reuse the `AnalyserNode`; drive 5 bars from **frequency-band** magnitudes
   (`getByteFrequencyData` split into 5 bins) so it reads like an equalizer AND reflects real input; keep the
   silence detection (overall RMS → "no sound" hint). Styled per spec (3x22px bars, meter gradient).
3. **Popovers anchor to their triggers** (absolute within a relative wrapper), not the mockup's fixed
   `right:250/158/70/16px` offsets - more robust and responsive. Keep the visual: `top` just below the bar,
   panel bg/border/radius/shadow/`popIn`, a single shared click-away backdrop, one popover open at a time,
   close on Escape.
4. **One PR** (a top-bar redesign is visually atomic - a half-migrated bar looks broken), built in the reviewed
   units below.

---

## Token layer (`apps/web/src/index.css`)

Add a `--hub-*` variable block. **Dark = the handoff's final values; Light = the paired palette below.** Define
lights in `:root`, dark overrides under `.dark` (the app toggles `.dark` on `<html>`). Reference them from
components via Tailwind arbitrary values `bg-[var(--hub-bar-bg)]` (or a thin set of `@theme` utilities if the
implementer prefers - either is fine; the variables are the source of truth).

| Token | Dark (spec) | Light (this plan) | Used by |
|---|---|---|---|
| `--hub-bar-bg` | `#0a1120` | `#ffffff` | bar background |
| `--hub-bar-border-top` | `rgba(120,150,220,.18)` | `rgba(47,107,237,.20)` | 2px top hairline |
| `--hub-bar-border-bottom` | `rgba(255,255,255,.05)` | `rgba(15,23,42,.08)` | 1px bottom border |
| `--hub-surface` | `#131d31` | `#f1f5f9` | elevated control bg (pill idle, chip, inputs) |
| `--hub-surface-hover` | `rgba(255,255,255,.06)` | `rgba(15,23,42,.05)` | icon-button hover |
| `--hub-popover-bg` | `#0e1729` | `#ffffff` | popover/panel bg |
| `--hub-popover-border` | `rgba(255,255,255,.11)` | `rgba(15,23,42,.10)` | popover border |
| `--hub-popover-shadow` | `0 24px 60px rgba(0,0,0,.6)` | `0 16px 40px rgba(15,23,42,.16)` | popover shadow |
| `--hub-border` | `rgba(255,255,255,.10)` | `rgba(15,23,42,.10)` | default control border |
| `--hub-border-hover` | `rgba(120,150,220,.5)` | `rgba(47,107,237,.45)` | control hover border |
| `--hub-text` | `#eef2f8` | `#0f172a` | primary text |
| `--hub-text-2` | `#c7d0e0` | `#334155` | secondary text / icon buttons (`#c7d0e0`→`#475569`) |
| `--hub-muted` | `#7c8aa3` | `#64748b` | muted labels/chevrons |
| `--hub-muted-2` | `#6b7890` | `#94a3b8` | footer notes / empty states |
| `--hub-placeholder` | `#5e6b82` | `#94a3b8` | input placeholder |
| `--hub-blue` | `#2f6bed` | `#2f6bed` | primary blue (both) |
| `--hub-blue-soft-bg` | `rgba(47,107,237,.14)` | `rgba(47,107,237,.10)` | selected row / active chip bg |
| `--hub-blue-soft-border` | `rgba(47,107,237,.30)` | `rgba(47,107,237,.30)` | active chip border |
| `--hub-blue-text` | `#cfe0ff` | `#1e40af` | active chip / accent text (`#8fb0ff`→`#1d4ed8`) |
| `--hub-red` | `#e5484d` | `#e5484d` | record red (both) |
| `--hub-red-hover` | `#ef5a5f` | `#ef5a5f` | stop hover |
| `--hub-red-text` | `#ff8b8f` | `#dc2626` | timer text (`#ff5b60`/`#ff7b7f`→`#dc2626`/`#b91c1c`) |
| `--hub-red-soft-bg` | `rgba(229,72,77,.12)` | `rgba(229,72,77,.08)` | recording pill bg |
| `--hub-red-soft-border` | `rgba(229,72,77,.30)` | `rgba(229,72,77,.30)` | recording pill border |
| `--hub-green` | `#22c55e` | `#16a34a` | system-audio green (slightly darker on light for contrast) |
| `--hub-green-text` | `#22c55e` | `#15803d` | "+System" pill text |
| `--hub-green-soft-bg` | `rgba(34,197,94,.14)` | `rgba(34,197,94,.12)` | "+System" pill / toggle row bg |
| `--hub-green-soft-border` | `rgba(34,197,94,.22)` | `rgba(34,197,94,.30)` | system toggle row border |
| `--hub-meter-gradient` | `linear-gradient(180deg,#e5484d,#f2c94c,#22c55e)` | same | level meter bars |

Static (theme-independent): logo `conic-gradient(from 210deg,#2f6bed,#22c55e,#f59e0b,#2f6bed)`; keep the real
`/logo.png` per the handoff ("replace the placeholder with the real Diariz logo") - render it at 34x34 with the
"Diariz" wordmark (`700 21px`, `letter-spacing:-.01em`), 12px gap.

- [ ] **Task 0:** add the `--hub-*` block to `index.css` (both themes). No test; verified visually in later
  tasks. Commit.

---

## File structure

**Modify**
- `apps/web/src/index.css` - the `--hub-*` token layer.
- `apps/web/src/components/TopBar.tsx` - the 80px hub frame (logo · spacer · cluster · spacer · avatar).
- `apps/web/src/components/Recorder.tsx` - render the new control cluster + popovers from existing state
  (logic unchanged; JSX restructured). Extract sub-views into small presentational components (below) to keep
  it readable.
- `apps/web/src/components/UserMenu.tsx` - avatar (46px) + account popover restyle.
- `apps/web/src/components/Recorder.test.tsx`, `UserMenu.test.tsx` - update structural assertions.

**New (presentational, driven by Recorder's existing state via props)**
- `apps/web/src/components/hub/RecordHero.tsx` - idle pill + recording pill (timer, meter, pause, stop).
- `apps/web/src/components/hub/HubLevelMeter.tsx` - the 5-bar audio-driven meter.
- `apps/web/src/components/hub/AudioSourcePopover.tsx` - mic select + system toggle + processing chips + note.
- `apps/web/src/components/hub/AutoStopPopover.tsx` - the auto-stop option rows.
- `apps/web/src/components/hub/NotesPopover.tsx` - the notes-while-recording popover (wraps `NotesSection`).
- `apps/web/src/components/hub/HubPopover.tsx` - shared popover shell (backdrop, `popIn`, anchor, Escape).
- `apps/web/src/components/hub/TopBar.test.tsx` - new shell coverage (none exists today).

(Keep the sub-components thin and prop-driven; `Recorder.tsx` remains the single stateful owner of the cluster,
passing state + handlers down. Do **not** duplicate MediaRecorder/audio logic.)

---

## Units (each: TDD, build green, commit; reviewed between)

### Unit 1 - Token layer + bar shell + shared popover
- `index.css` tokens (Task 0). `TopBar.tsx` → 80px frame, `padding:0 22px`, bar bg/borders from tokens, flex row
  `gap:16px` with two `flex:1` spacers, left→right order per spec, logo + wordmark. `HubPopover.tsx` shell
  (absolute, `top` below bar, panel tokens, `popIn` keyframe in index.css, shared `z-40` backdrop / `z-50`
  panel, Escape-to-close, anchored to a trigger ref). **Test:** `TopBar.test.tsx` renders logo/wordmark, has the
  cluster + avatar regions; `HubPopover` opens/closes on trigger + backdrop + Escape and only-one-open (a small
  harness test). Preserve `data-tour="capture"` on the cluster wrapper and `data-tour="account"` on the avatar.

### Unit 2 - Record hero (idle + recording) + real 5-bar meter
- `RecordHero.tsx`: IDLE pill (52px/`radius:26px`, red 36px circle + white dot, "Start recording" label);
  RECORDING pill (blink dot, mono timer `MM:SS` from `elapsed`, `HubLevelMeter`, 36px pause + 36px stop circles).
  Wire to existing `recording`/`paused`/`elapsed`/`start`/`pause`/`stop`. **Keep accessible names** `record`,
  `pause`, `resume`, `stop`. Add `blink`/`meter` keyframes to index.css.
- `HubLevelMeter.tsx`: reuse the `AnalyserNode` setup from `InputLevelMeter` but 5 bars driven by
  `getByteFrequencyData` split into 5 bins (write each bar's `scaleY` via ref in a rAF loop - no React
  re-render); keep `onSilentChange` (overall RMS via `lib/audioLevel.ts`) for the existing "no sound" hint.
  **Test:** idle shows the Start pill; toggling `recording` shows timer/meter/pause/stop with the right names;
  a `HubLevelMeter` unit test asserts it subscribes to a provided stream and renders 5 bars (mock `AnalyserNode`
  as the existing `InputLevelMeter.test.tsx` does).

### Unit 3 - Audio source chip + popover (mic + system + processing chips)
- The chip (44px, mic icon, "Audio source" label, green "+System" pill when `systemAudio`, chevron) opens
  `AudioSourcePopover`. Popover: MICROPHONE header + the existing mic `<select>` restyled as the full-width
  select button (**keep the `microphone` combobox** so its tests survive); "Capture system audio" toggle row
  (**keep `system audio` name**); PROCESSING header + the four constraint toggles rendered as **chips**
  (active = blue-soft + check; Mono inactive style) wired to the existing `toggleConstraint` (**keep the four
  checkbox accessible names**); footer note. **Test:** update `Recorder.test.tsx` source-selection group -
  chip opens the popover (`recorder-popover` or a new testid - keep one stable), the combobox order + persisted
  restore unchanged, the four processing chips toggle and feed `getStream` with the same constraints object,
  cog/chips disabled for "No microphone", system-audio checkbox + Combined/System/Microphone enum unchanged.

### Unit 4 - Auto-stop popover + Upload + Notes popover
- Auto-stop icon button (clock) opens `AutoStopPopover` (option rows: No auto-stop / 15 / 30 / 1h / "Stop at a
  set time…" revealing the `time` input), wired to existing `autoStopChoice`/`autoStopTime`/watcher. **Keep the
  `auto-stop` accessible name**; preserve the "stops at HH:MM" display + the paused-safe watcher behavior (its
  tests must still pass). Upload = icon button + the existing hidden `upload-input` (**unchanged**), styled
  44x44. Notes = pencil icon button **rendered only while recording**, opens `NotesPopover` (wraps the existing
  `NotesSection`/live-notes state: stamped lines, empty state, add-on-Enter, durability). Retire the floating
  `LiveNotesPanel` position in favor of the popover (reuse its inner logic). **Test:** auto-stop popover
  selection drives the schedule (fake timers, stops while paused); Notes button appears only when recording and
  the popover adds/stamps a line; upload still enqueues via `useUpload`.

### Unit 5 - Account menu (avatar + account popover)
- `UserMenu.tsx`: 46px avatar button (photo or initials, hover border) opens the account popover (308px):
  header (avatar + name + email), stats block (**Storage** with a 5px progress track filled to
  `storagePercent`, **Transcription used**) from the existing `getUserStorage`, menu rows (Preferences /
  Settings / Manage users / Manage platform formulas / Show tour / About - **same gating as today**), footer
  Sign out (red). Restyle only - **keep the storage/transcription assertions, gating, `z-50`, modal opening,
  and `data-tour="account"`**. **Test:** update `UserMenu.test.tsx` for the new structure while keeping the
  storage line, transcription line, gating, and sign-out assertions.

### Unit 6 - Responsive, a11y, integration polish
- Responsive (per spec): at narrow widths drop the "Audio source" text to icon + "+System" only, collapse the
  record hero label; keep the record control + avatar always visible (CSS container/media queries or a width
  hook). Confirm one-popover-open, backdrop + Escape close, focus handling. Ensure the recording-state cluster
  reflows without layout jump. **Test:** a `TopBar`/`Recorder` integration test for the end-to-end idle→record→
  notes-appears→stop flow and popover exclusivity; a narrow-width assertion if feasible with jsdom (else note
  it as manual/visual).
- **Live verification (Browser pane):** run the dev server, drive idle→recording, open each popover, toggle a
  processing chip + system audio, set an auto-stop, add a note, open the account menu; screenshot light AND
  dark (`resize_window` colorScheme) to confirm both palettes. Fix any visual drift vs. the `.dc.html`.

### Unit 7 - Release + docs
- **Version:** this is a substantial UX enhancement → **Minor bump** (`version.json` + 3 mirrors). `RELEASES[0]`
  entry (headline "Redesigned recording top bar", summary of the command hub; `changed` bullets). No em/en
  dashes.
- **Capabilities/README/features:** no capability changed (same features, new presentation) → **no**
  `CAPABILITIES`/README/`features.md` edit; the About disclaimers are unchanged (no new library). Say so in the
  PR. (If the user wants the redesign called out in README, add one line - otherwise skip.)
- **Architecture/schema docs:** none - purely web presentation. State that in the PR.
- **Deployment surface:** **web-only, server redeploy** (the desktop shell loads the web app) - no desktop
  release. Note it.

---

## Self-review notes
- **Spec coverage:** bar shell (U1), record hero idle/recording + meter (U2), audio-source chip + popover +
  processing chips (U3), auto-stop + upload + notes popovers (U4), account menu + stats (U5), popovers/animations/
  responsive/a11y (U1+U6), tokens both themes (U1). Covered.
- **No new features:** every control maps to existing state/logic; only `HubLevelMeter` is genuinely new
  rendering (still reuses the existing AnalyserNode/audioLevel).
- **Test-contract risk:** preserve the accessible names + `upload-input` + the `getStream` constraints shape;
  update structural assertions. Keep `data-tour` anchors so the guided tour still lands.
- **Open for user review:** the **invented light palette** table above - adjust any value before implementation.

## Verification (whole redesign)
- **Web unit/RTL:** the updated `Recorder.test.tsx`/`UserMenu.test.tsx`, new `TopBar.test.tsx`/`HubPopover`/
  `HubLevelMeter` tests; full `npx vitest run` green; `npm run build` clean. The pure-module tests
  (`audioDevices`/`audioSource`/`recorderSchedule`/`recorderTiming`/`audioLevel`/`trayRecorder`) must stay green
  untouched (proof the logic wasn't disturbed).
- **Live (Browser pane):** idle + recording states and all four popovers match the `.dc.html`, in **both**
  light and dark themes; recording meter reacts to real input; auto-stop/notes/system-audio still function.
