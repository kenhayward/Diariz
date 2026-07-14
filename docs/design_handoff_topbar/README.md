# Handoff: Diariz Top Bar — "Command Hub" redesign

## Overview
A redesign of the Diariz web app's global top bar — the persistent control strip for recording/transcription. It replaces today's flat row of controls with a **record-centric command hub**: one prominent Record control that morphs into the live recording state, with all audio-input configuration consolidated behind a single "Audio source" chip. Covers the idle state, the active-recording state, and four popovers (audio source, auto-stop, notes-while-recording, account menu).

## About the Design Files
The file in this bundle (`Diariz Top Bar.dc.html`) is a **design reference created in HTML** — a working prototype demonstrating intended look and behavior. It is **not production code to copy directly**. Diariz web is a real application; the task is to **recreate this design in that codebase's existing environment** (its framework, component library, state layer, and styling conventions). If the top bar already exists as a component there, refactor it to match this spec rather than adding a parallel implementation.

Note: the prototype is authored as a "Design Component" (custom `<sc-if>` tags + a small logic class). That is a prototyping format only — ignore the mechanism and reproduce the **markup structure, styling, and behavior** using the app's real patterns.

## Fidelity
**High-fidelity.** Colors, typography, spacing, radii, shadows, and interactions are final and intended to be matched precisely. All values are listed under Design Tokens.

## Layout — the bar
- Full-width horizontal bar, **80px tall**, `padding: 0 22px`, `box-sizing: border-box`.
- Background `#0a1120`; **2px top border** `rgba(120,150,220,.18)` (subtle blue hairline); 1px bottom border `rgba(255,255,255,.05)`.
- Single flex row, `align-items:center`, `gap:16px`. Two flexible spacers (`flex:1`) — one after the logo, one before the avatar — center the control cluster and pin the avatar right.
- Left→right order: **Logo** · spacer · **Record hero** · **Audio source chip** · **Auto-stop** (icon) · **Upload** (icon) · **Notes** (icon, recording-only) · spacer · **Account avatar**.

### Components

**Logo**
- 34×34 rounded square (`border-radius:9px`), `conic-gradient(from 210deg, #2f6bed, #22c55e, #f59e0b, #2f6bed)`, shadow `0 2px 8px rgba(47,107,237,.4)`. Three white waveform bars (2.5px wide, heights 8/14/6px, `gap:2px`) anchored to the bottom.
- Wordmark "Diariz" — `system-ui 700 21px`, `#fff`, `letter-spacing:-.01em`. 12px gap between mark and wordmark.

**Record hero — IDLE**
- Pill button, height 52px, `border-radius:26px`, `padding:0 8px`, bg `#131d31`, border `1px solid rgba(255,255,255,.12)`. Hover: border → `rgba(120,150,220,.5)`.
- Left: 36px red circle (`#e5484d`, shadow `0 3px 12px rgba(229,72,77,.5)`) containing a 13px white dot.
- Label "Start recording" — `system-ui 600 16px`, `#fff`, `padding-right:14px`, 13px gap.

**Record hero — RECORDING** (replaces idle in place)
- Pill, height 52px, `border-radius:26px`, `padding:0 8px 0 16px`, bg `rgba(229,72,77,.12)`, border `1px solid rgba(229,72,77,.3)`, `gap:14px`.
- Blinking dot: 9px circle `#ff5b60`, `blink` animation (1.2s, opacity 1→.25→1).
- Timer: `ui-monospace 600 18px`, `#ff8b8f`, `font-variant-numeric:tabular-nums`. Format `MM:SS`, counts up from `00:00` at 1s intervals.
- Level meter: 5 bars, each 3px wide, full 22px height, `border-radius:2px`, `background:linear-gradient(180deg,#e5484d,#f2c94c,#22c55e)`, `transform-origin:bottom`, `gap:2px`. Each animates `meter` (0.7s ease-in-out infinite, scaleY .25→1→.25) with staggered negative delays (`0, -.2s, -.38s, -.5s, -.29s`) so bars are out of phase.
- Pause button: 36px circle, bg `rgba(255,255,255,.08)` (hover `.16`), pause-bars icon `#eef2f8`.
- Stop button: 36px circle, bg `#e5484d` (hover `#ef5a5f`), shadow `0 3px 12px rgba(229,72,77,.5)`, filled-square icon `#fff`. Stops recording.

**Audio source chip**
- Height 44px, `border-radius:11px`, `padding:0 14px`, bg `#131d31`, border `1px solid rgba(255,255,255,.1)`. Hover: border → `rgba(120,150,220,.5)`. `gap:10px`.
- Mic icon (stroke `#8fb0ff`), label "Audio source" (`system-ui 500 14.5px`, `#eef2f8`), a green "+System" pill (`system-ui 500 12px`, text `#22c55e`, bg `rgba(34,197,94,.14)`, `padding:2px 7px`, `border-radius:6px`) shown when system audio is on, and a chevron-down (stroke `#7c8aa3`). Opens the Audio source popover.

**Icon buttons — Auto-stop, Upload, Notes**
- 44×44, `border-radius:11px`, bg transparent, border `1px solid rgba(255,255,255,.1)`, icon color `#c7d0e0`. Hover bg `rgba(255,255,255,.06)`.
- Auto-stop = clock icon (opens Auto-stop popover). Upload = tray/arrow icon. Notes = pencil/edit icon, **only rendered while recording** (opens Notes popover). Icons are 18px, `stroke-width:2`, round caps/joins.

**Account avatar**
- 46px circle, `border:2px solid rgba(255,255,255,.14)`, placeholder fill `linear-gradient(135deg,#6b8cae,#3a5068)` (swap for the user's real photo). Hover border → `rgba(120,150,220,.6)`. Opens Account menu.

## Popovers
All popovers: absolute-positioned, `top:88px` (just below the 80px bar), bg `#0e1729`, border `1px solid rgba(255,255,255,.11)`, `border-radius:14px`, shadow `0 24px 60px rgba(0,0,0,.6)`, `popIn` animation (0.14s, fade + translateY -6px→0), `z-index:50`. A full-screen click-away backdrop `rgba(4,8,15,.45)` at `z-index:40` closes any open popover. Only one popover open at a time.

Section headers inside popovers: `system-ui 600 11px`, `letter-spacing:.09em`, `text-transform:uppercase`, color `#7c8aa3`.

**Audio source popover** (width 352px, `right:250px`, `padding:16px`)
- "MICROPHONE" header → full-width select-style button (44px, bg `#131d31`, border `rgba(255,255,255,.1)`) showing "Microphone (default)" + chevron.
- "Capture system audio" toggle row: 20px green (`#22c55e`) rounded checkbox with dark check, on a `rgba(34,197,94,.08)` tinted row bordered `rgba(34,197,94,.22)`.
- "PROCESSING" header → wrap of chip toggles. Active chips (Echo cancel, Noise suppress, Auto gain): `padding:8px 11px`, `border-radius:9px`, bg `rgba(47,107,237,.14)`, border `rgba(47,107,237,.3)`, text `#cfe0ff`, with a small check icon. Inactive chip (Mono): transparent, border `rgba(255,255,255,.14)`, text `#8a95a9`.
- Footer note (top-bordered): "Processing applies to microphone capture only." — `system-ui 400 12px`, `#6b7890`.

**Auto-stop popover** (width 280px, `right:158px`, `padding:10px`)
- Header "AUTO-STOP RECORDING". Options as 11px-padded rows, `border-radius:9px`, `system-ui 500 14.5px`. Selected row: bg `rgba(47,107,237,.14)`, text `#eef2f8`, trailing blue check. Options: **No auto-stop** (selected), Stop in 15 minutes, Stop in 30 minutes, Stop in 1 hour, and (top-bordered, text `#8fb0ff`) "Stop at a set time…". Unselected rows text `#c7d0e0`, hover bg `rgba(255,255,255,.05)`.

**Notes popover** (width 400px, `right:70px`, `padding:18px`)
- Title "Notes while recording" (`system-ui 700 17px`, `#fff`) with a blinking red dot.
- Subtitle "Each line is stamped at the moment you press Enter." (`#7c8aa3 400 13px`).
- Input row: 44px field (bg `#131d31`, border `rgba(255,255,255,.1)`, placeholder "Add a note…" `#5e6b82`) + 64px "Add" button (bg `#2f6bed`, `#fff 600 14px`). Enter or Add appends a timestamped note.
- Empty state: "No notes yet. Jot trigger phrases here — they steer the meeting minutes." (`#6b7890 400 13px`).

**Account menu** (width 308px, `right:16px`, `overflow:hidden`)
- Header (bottom-bordered): 46px gradient avatar + "Platform Administrator" (`#fff 700 16px`) + "admin@diariz.app" (`#7c8aa3 400 13px`).
- Stats block (bottom-bordered): **Storage** label + "1.9 / 50 GB", with a 5px progress track (`rgba(255,255,255,.08)`) filled 4% in `#2f6bed`; **Transcription used** + "42:38". Labels `#aab6ca 500 12.5px`, values `#7c8aa3`.
- Menu items (10px-padded rows, `border-radius:9px`, `#eef2f8 500 14.5px`, hover bg `rgba(255,255,255,.06)`): Preferences, Settings, Manage users, Manage platform formulas, Show guided tour, About.
- Footer (top-bordered): **Sign out** — text `#ff7b7f 600 14.5px`, hover bg `rgba(229,72,77,.1)`.

## Interactions & Behavior
- **Start recording**: click the idle hero → bar enters recording state. Idle pill is replaced by the recording pill (timer + meter + pause + stop); the **Notes** icon button appears. Timer counts up `MM:SS` every second from `00:00`.
- **Stop**: click the stop square (or, if implemented, Pause) → returns to idle; timer resets; Notes button disappears.
- **Popovers**: clicking Audio source / Auto-stop / Notes / Avatar toggles that popover. Opening one closes any other. Click the backdrop (anywhere outside) to close. (Also close on Escape in production.)
- **Animations**: meter bars `meter` 0.7s ease-in-out infinite (staggered); recording/notes dots `blink` 1.2s; popovers `popIn` 0.14s ease.
- **Responsive**: at narrower widths, drop the "Audio source" text label to just the mic icon + "+System" pill, and collapse the record hero label; keep the record control and avatar always visible. (The prototype shows the wide layout.)

## State Management
- `isRecording: boolean` — drives hero swap + Notes button visibility.
- `elapsedSeconds: number` — incremented by a 1s interval while recording; reset to 0 on start/stop; formatted `MM:SS`. Clear the interval on stop and on unmount.
- `openPopover: 'source' | 'stop' | 'notes' | 'acct' | null` — single active popover.
- Backing data to wire to real APIs: selected microphone, systemAudio on/off, processing flags (echoCancellation, noiseSuppression, autoGain, mono), autoStop selection, notes list (each `{ text, timestamp }`), and account/usage info (name, email, storageUsed/Total, transcriptionUsed).

## Design Tokens
Colors:
- App/canvas bg `#050912`; bar bg `#0a1120`; popover/panel bg `#0e1729`; elevated control bg `#131d31`.
- Primary blue `#2f6bed`; blue accents `#8fb0ff` / `#5b8dff` / `#cfe0ff`.
- Record red `#e5484d` (hover `#ef5a5f`); red text/dot `#ff5b60` / `#ff8b8f` / `#ff7b7f`.
- System-audio green `#22c55e`.
- Text: primary `#eef2f8` / `#fff`; secondary `#c7d0e0` / `#aab6ca`; muted `#7c8aa3` / `#6b7890` / `#8a95a9`; placeholder `#5e6b82`.
- Borders: `rgba(255,255,255,.05 / .07 / .1 / .11 / .12 / .14)`; blue hairline `rgba(120,150,220,.18)`.
- Meter gradient `linear-gradient(180deg,#e5484d,#f2c94c,#22c55e)`; logo `conic-gradient(from 210deg,#2f6bed,#22c55e,#f59e0b,#2f6bed)`; avatar placeholder `linear-gradient(135deg,#6b8cae,#3a5068)`.

Spacing: bar padding 22px; row gap 16px; control gaps 10–14px; popover padding 10/16/18px.

Radii: bar controls 11px; record pill 26px (height/2); circular buttons 50%; popovers 14px; chips/inputs 9–10px; checkboxes 6px; logo 9px.

Type: family `system-ui, "Segoe UI", sans-serif` (mono `ui-monospace, Menlo, monospace` for timer). Sizes 11 / 12 / 12.5 / 13 / 13.5 / 14 / 14.5 / 15 / 16 / 17 / 18 / 21px. Weights 400 / 500 / 600 / 700.

Shadows: bar controls `0 3px 12px rgba(229,72,77,.5)` (record) ; popovers `0 24px 60px rgba(0,0,0,.6)`; logo `0 2px 8px rgba(47,107,237,.4)`.

Sizes: bar height 80px; hero/recording pill 52px; standard controls 44px; icon buttons 44×44; avatar 46px; logo 34px; icons 16–18px; meter bars 3×22px.

## Assets
- **Icons** are inline SVGs (Feather-style, 24×24 viewBox, `stroke-width:2`, round caps/joins): microphone, chevron-down, clock, upload, edit/pencil (notes), pause (two filled rects), stop (filled rounded square), check. Use the codebase's existing icon set (Feather/Lucide equivalents) rather than copying these paths.
- **Logo** is a CSS placeholder (gradient + waveform bars). Replace with the real Diariz logo asset.
- **Avatar** is a gradient placeholder. Replace with the user's real profile image.

## Files
- `Diariz Top Bar.dc.html` — the high-fidelity prototype (idle + recording states, all four popovers, interactive). Reference it alongside this README; the README is self-sufficient for implementation.
