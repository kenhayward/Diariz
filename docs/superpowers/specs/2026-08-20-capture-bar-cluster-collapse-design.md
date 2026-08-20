# The capture bar's cluster collapses harder in a narrow window

**Date:** 2026-08-20
**Status:** approved, ready to plan
**Deployment surface:** server redeploy (web only). No desktop release - no `apps/desktop` file is touched.
**Version:** 0.233.1 -> 0.234.0 (functional enhancement: Minor +1, Build reset)

## Problem

The capture bar centres its record cluster over the content column, and that column shrinks with the
window. Below a point the cluster no longer fits and spills past the column's right edge. PR #560 stopped
that spill being *drawn over* the chat panel, but the controls in it are now hidden behind the panel
instead - so at a narrow window the auto-stop and upload buttons become unreachable until the window is
widened or the chat panel collapsed.

The existing collapse is one step: at `@xl` (a 576px bar) the two text labels - "Audio source" and "Start
recording" - drop, leaving icons. That single step is not enough, and it is tuned for the idle cluster.
The recording cluster is roughly 290px wider and gets no extra help at all.

Measured widths (real strings, real fonts, dimensions read from source; measured in a browser with
`canvas.measureText`, so they are exact rather than estimated):

| State | Bar width the cluster needs today |
|---|---|
| Idle, labels shown | 609px (which is why `@xl` = 576px is where they drop) |
| Idle, labels hidden - **today's floor** | **376px** |
| Recording, labels hidden (desktop: + camera + notes) | **666px** |

A 666px bar with the left panel collapsed to its rail and the chat panel open is a ~1030px window. That is
an ordinary window size, so the recording cluster is the real offender.

## Goals

1. No control in the cluster becomes unreachable at any window width. Something the user can click always
   leads to every action the bar offers.
2. The collapse is progressive - chrome is shed before structure changes.
3. The idle cluster and the recording cluster each collapse at the width where *they* stop fitting, not at
   one shared guess.

## Non-goals

- Clipping the bar. `overflow-hidden` on the cluster would cut off the recorder's popovers, which are
  absolute children of its own `relative` root (`CaptureBar.tsx` says so explicitly). The spill stays; it is
  simply prevented from happening.
- Changing what any control does, or its accessible name.
- A minimum window width on the Electron shell, or the chat panel collapsing itself. Both were considered
  during design and rejected: the first does not help the web, the second moves the user's panel out from
  under them while they drag.
- Reworking the `@xl` label step. It stays exactly as it is.

## Design

### Four tiers, driven by the bar's container width

The bar is already a `@container` and the existing labels already ask it (not the viewport) how much room
there is. This design adds two more tiers to that same mechanism.

| Tier | What changes | Idle floor | Recording floor (desktop) |
|---|---|---|---|
| **A** roomy (`≥576`, today) | labels shown | 609px | - |
| **B** snug (`<576`, today) | labels hidden | 376px | 666px |
| **C** tight | `+System` pill becomes a green dot on the mic; chevron hidden; bar padding and gaps tightened; the level meter drops from the recording pill; Upload hides while recording | **226px** | **421px** |
| **D** cramped | the secondary controls fold into a `...` overflow button | **176px** | **321px** |

Thresholds, chosen from those measurements with slack:

|  | tight | cramped |
|---|---|---|
| Idle | `< 400px` | `< 240px` |
| Recording | `< 690px` | `< 440px` |

### Why container queries and not JavaScript measurement

Below `@xl` the cluster contains **no translated text**. Everything is an icon, plus the hardcoded
`+System` string and a monospace timer. So a fixed pixel threshold below `@xl` is exact and stable across
locales, which is what makes the CSS approach honest here rather than a guess.

The one exception is the recording pill's "Paused" label (`recPaused`), which replaces the level meter
while paused and does vary by locale - about 20px between English and Spanish. The thresholds carry more
slack than that.

A JS `ResizeObserver` tier hook was considered. It would be self-correcting rather than hand-tuned, but it
adds a measurement loop with hysteresis to avoid oscillation, cannot be tested in jsdom at all (no layout),
and introduces a second responsive mechanism alongside the container queries already in use. It is not
warranted while the thresholds are exact.

### Per-state thresholds without dynamic class names

Tailwind generates utilities by scanning source for **complete** class strings, so a concatenated variant
(`` `${prefix}hidden` ``) is never generated. Both literal strings are therefore written out and selected
by the `recording` flag:

```tsx
const HIDE_WHEN_CRAMPED = recording ? "@max-[440px]:hidden" : "@max-[240px]:hidden";
```

### The overflow menu does not nest popovers

`HubPopover` renders its panel `absolute top-[calc(100%+8px)]` inside its own `relative` wrapper in the
bar - the panel drops from the bar, not from its trigger. So a menu row does not need to host the control's
popover: it just calls `hub.toggle("stop" | "notes")`, and the hub's existing one-open-at-a-time rule
closes the menu while the real popover opens from the bar exactly as it does today.

The overflow menu is a new hub popover id, `"more"`. Its rows:

| Row | State | Action |
|---|---|---|
| Auto-stop | always | `hub.toggle("stop")` |
| Upload | idle only | close menu, open the file dialog |
| Screenshot | recording, desktop shell, area-gated | close menu, `requestCapture()` |
| Notes | recording | `hub.toggle("notes")` |

Upload is absent while recording for the same reason it is hidden inline at tier C: it is already disabled
in that state, so it is dead weight rather than a lost action.

Each row carries the same disabled state as its inline button, and the same reason text where there is one
(the screenshot row is inert with "Set a capture area first" until an area exists).

The audio-source chip and the record hero never fold into the menu. They are the bar's two primary
controls and are what the floors in the tier table are built around: 44px + 52px at tiers C and D.

### Three mechanical constraints the implementation must respect

1. **Hide the button, never its `relative` wrapper.** The wrapper hosts the popover; hiding it would hide
   the popover the menu is trying to open. The wrapper stays in flow and collapses to zero width, since its
   only remaining child is absolutely positioned.
2. **The hiding class goes on a wrapper element, not on `HubIconButton`.** That component sets
   `display: flex` as an inline style, which beats a `hidden` class. Giving it a `className` prop was
   considered; a wrapper is less invasive to a component shared by five call sites.
3. **The level meter is hidden, not unmounted.** `HubLevelMeter` is what detects silence - it drives
   `onSilentChange`, and that is what raises the "no sound" hint during a recording. Dropping it from the
   tree at tier C would silently disable that hint at narrow widths. It stays mounted and is hidden with
   CSS, so it keeps listening.

## Testing

**Real behaviour, jsdom (`Recorder.test.tsx`):** the overflow menu is ordinary logic and gets ordinary
tests - the rows present in each state, Auto-stop and Notes opening their popovers, Upload opening the file
dialog, Screenshot gated on a capture area, and the menu closing when a row is chosen.

**Class contract, jsdom:** the tier classes are class-presence assertions and prove only that the classes
are present - jsdom computes no geometry and no Tailwind CSS is loaded. They are worth pinning (a deleted
class is a silent regression) and are documented in the test as proving nothing about layout.

**The floors, browser:** the eight numbers in the tier table are the actual claim of this change, and they
get measured in a browser against the app's real classes and compiled CSS - the same method used to verify
PR #560.

### A known jsdom consequence

The `...` button is always in the DOM, hidden by CSS above tier D. In a browser it is `display:none` and so
out of the accessibility tree; in jsdom, where no CSS is applied, it is present and visible. That is
harmless for existing tests (its accessible name is new and collides with nothing), but a test that opens
the menu sees both the menu's "Auto-stop" row and the inline "Auto-stop" button. New tests scope their
queries with `within(dialog)`.

## Release checklist

- Version 0.233.1 -> 0.234.0 in `version.json` and its four mirrors.
- `RELEASES[0]` entry.
- No scope change: the bar offers the same controls and does the same things, so no `CAPABILITIES` /
  README / `docs/features.md` edit. No architecture or schema change.
- The help article on recording is untouched: no behaviour a user relies on changes, only where a control
  is drawn at a narrow width.
