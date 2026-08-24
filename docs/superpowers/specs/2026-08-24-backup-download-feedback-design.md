# Backup download feedback (desktop)

Date: 2026-08-24
Status: approved, ready for planning
Version: 0.244.1 -> 0.245.0

## Problem

A Platform Administrator who downloads a backup from the **desktop app** cannot tell how far the
download has got, or whether it finished successfully.

Three distinct causes, found by reading the path end to end:

1. **The feedback stops exactly where the long part begins.** `MaintenanceController.Backup` builds
   the entire zip to a temp file before sending a single byte, then returns `File(...)`.
   `IBackupProgress` tracks only that build, and the tracking scope is disposed the moment the zip is
   finished (`MaintenanceController.cs:73`). `MaintenancePanel.tsx:71-72` therefore flips to the green
   `backupReady` message at the *start* of the transfer. On a multi-GB archive that is several more
   minutes with nothing reported.

2. **The desktop shell has no downloads UI at all.** `apps/desktop/src/main.js` never handles
   `will-download`. Electron's default is a Save-As dialog followed by a silent write to disk: no
   shelf, no progress, no completion notice. The panel's advice - *"Backup ready - check your
   browser's downloads"* (`apps/web/src/locales/en/account.json:330`) - is literally unfollowable in
   the desktop shell. In a real browser the download shelf covers the gap, which is why this reads as
   a desktop-only problem.

3. **A failed build reports success.** If `pg_dump` fails and the request 500s, the server goes idle
   with `sawBuildRunning` already true, so the panel shows the green "Backup ready". An anchor-href
   download cannot surface a 500 either, so a failed backup is indistinguishable from a good one.
   This is a correctness bug, not missing polish.

A related hazard found while scoping (3): the anchor at `MaintenancePanel.tsx:140` has neither a
`download` nor a `target` attribute. On a 200 the `Content-Disposition: attachment` header makes it a
download, but on a **500 the main window navigates to the error body** - in the desktop shell the app
is replaced by an error page until reload.

## What makes this tractable

`File(stream, ...)` on a seekable `FileStream` sets `Content-Length`, so the transfer already carries
a known total. Real percentage progress needs no server change.

## Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Where feedback lives | Desktop shell `will-download` handler | The shell is what removed the browser's download UI; fixing it there fixes every download in the app, permanently. Costs a desktop release. |
| False-success bug | Fixed in the same PR | It is the other half of "did it work?"; splitting it leaves the question half-answered. |
| Visible where | Maintenance panel + OS notification | Inline progress while you are looking; a notification for a long download that finishes while you are elsewhere. |
| Save location | Unchanged Save-As dialog | A multi-GB archive containing every password hash on the platform should land where the admin chose. The handler only adds reporting. |
| Handler scope | Every download, panel filters by URL | One generic handler; audio, transcript and formula-result downloads get completion feedback for free. |

## Architecture

Six pieces, following the shell's existing pure-model + IPC-bridge pattern (`recorderState.js`,
`updateState.js`, `screenshotState.js`).

### Desktop shell

**1. `apps/desktop/src/downloadState.js`** (new, pure)

Arithmetic and shell-owned copy, so both are unit-testable without Electron:

- `downloadProgress({ received, total })` -> `{ phase, percent, sizeText }`, where `phase` is
  `"starting"` or `"progressing"` and `percent` is `null` when the total is unknown.
- `notificationForDownload(state, { filename, elapsedMs })` -> `{ title, body }` or `null`.

**The model returns numbers, not sentences.** The handler is generic across every download, and the
shell has no i18n, so user-facing progress copy must be composed in the panel from the web app's
locale catalogs. The shell owns only the notification text, which is already English-only in
`updateState.js` and `recorderState.js` and stays consistent with them.

**2. `apps/desktop/src/main.js`**

One `session.on("will-download")` registered in `createWindow()`, alongside the existing
`setDisplayMediaRequestHandler`. It assigns an id, sends `started` / `progress` / `done` to the
renderer over a single `download:event` channel, and raises the notification from the pure model on
`done`. It deliberately does **not** call `setSavePath`, so the Save-As dialog is unchanged.

The handler is registered on `mainWindow.webContents.session`, the default session, which the notes
pop-out window shares - so downloads from either window are covered.

**3. `apps/desktop/src/preload.js`**

`onDownloadEvent(cb)` returning an unsubscribe function, matching every other subscription there.

### Web

**4. `apps/web/src/lib/desktopDownloads.ts`** (new)

`watchDesktopDownloads(bridge, matchUrl, handlers)`. Degrades to a no-op disposer when
`window.diariz` is absent, so the browser build is untouched. Typings added to
`apps/web/src/diariz.d.ts`.

**5. `apps/web/src/components/MaintenancePanel.tsx`**

A fourth state after `building`: `transferring` -> `saved` / `failed`. New i18n strings in all four
locale catalogs (en, fr, es, de). Plain hyphens only - no em/en dashes in user-facing text.

Adds the `download` attribute to the backup anchor.

### Server

**6. `src/Diariz.Api/Services/BackupProgress.cs`**

`BackupProgressSnapshot` gains `LastOutcome` (`Completed` / `Failed` / null). `Begin()` returns a
scope carrying an explicit `Succeeded()`; disposing without calling it records a failure. This is the
standard commit-or-rollback shape, and it is what lets the panel stop showing green for a `pg_dump`
that blew up.

`MaintenanceController.Backup` calls `Succeeded()` immediately before returning `File(...)`.

## Data flow

```
click Download
  |- panel: building...                  (polls /backup/status, as today)
  |  server finishes zip -> LastOutcome = Completed
  |- response headers sent -> Electron fires will-download
  |  main.js -> renderer: { type: "started", id, url, filename, totalBytes }
  |- panel: Downloading backup - 43% of 2.1 GB    (progress events)
  |- main.js -> renderer: { type: "done", state: "completed", savePath }
  |- panel: Backup saved to C:\...\diariz-backup-20260824-141203-V0_245_0.zip
     + OS notification
```

### Two independent inputs; download events win

The poll and the download events are separate signals and can interleave either way at a 1.5s poll
interval. Rather than sequencing them, the panel treats a download event for the backup URL as
authoritative: once one arrives it overrides whatever the poll says. The poll's remaining jobs are
build progress and the failure verdict.

### Message derived from the numbers, not an assumed sequence

Electron fires `will-download` *before* the Save-As dialog resolves. The copy must not depend on
exactly when bytes start flowing relative to the user picking a folder:

| Condition | `downloadProgress` returns | Panel renders (en) |
|---|---|---|
| `received === 0` | `phase: "starting"` | "Starting download..." |
| `total > 0` | `percent: 43, sizeText: "2.1 GB"` | "Downloading backup - 43% of 2.1 GB" |
| `total <= 0` | `percent: null, sizeText: "412 MB"` | "Downloading backup - 412 MB" |

Correct whichever way the dialog and the byte stream interleave, and `total <= 0` also covers a
response arriving without `Content-Length`.

### Notification noise rule

Because the handler covers every download, a 5 KB transcript completing would otherwise fire an OS
notification - worse than the current silence. The rule:

- **`completed`**: notify only when the download ran longer than **5 seconds**.
- **`interrupted`**: always notify, regardless of size or duration.
- **`cancelled`**: never notify - the user did that themselves.

A failure should surface regardless of size; a quick download finishes before you have looked away.

## Error handling

| Case | Behaviour |
|---|---|
| Build fails (`pg_dump` dies) | Scope disposed without `Succeeded()` -> `LastOutcome = Failed` -> panel shows a red failure line, not green. The `download` attribute keeps the 500 body from navigating the shell away from the app. |
| Transfer interrupted (server restart, disk full) | `done` with `interrupted` -> red "Backup download failed", plus a notification. |
| User cancels the Save dialog | `done` with `cancelled` -> panel returns to idle silently. |
| No download event ever arrives (plain browser, blocked download) | Existing grace-window behaviour untouched; the browser keeps its current "check your browser's downloads" copy, because there the download shelf genuinely does the job. |

## Testing

TDD per layer, red first.

### `apps/desktop/src/downloadState.test.js` (node --test, no Electron)

`downloadProgress`: zero received -> `phase: "starting"`; known total -> the right `percent` and
`sizeText`; `total <= 0` -> `percent: null` with a `sizeText` still present; received slightly over
total (Chromium overshoots) -> clamps at 100 rather than reporting 101.

`notificationForDownload`: the load-bearing pair is **completed after 30s -> notifies** vs
**completed after 1.2s -> null**, alongside **interrupted after 200ms -> still notifies**. That last
case catches an implementation that gates everything on elapsed and swallows fast failures.
`cancelled` -> null.

### `apps/web/src/lib/desktopDownloads.test.ts` (vitest)

No `window.diariz` -> returns a disposer, nothing throws, handlers never fire (the guard that keeps
the browser build working). With a fake bridge: events matching the URL predicate reach the handlers
and events for a *different* URL do not; after dispose, nothing does.

Written as call assertions on a fake bridge, never by omitting a method from a mock - an omitted
method is a guard that quietly dies the moment something else in the tree needs it.

### `apps/web/src/components/MaintenancePanel.test.tsx` (vitest + RTL)

Plain assertions - there is no jest-dom in this repo.

The two that must fail against today's code:

- With a desktop bridge present, once the server goes idle the panel shows **transfer progress, not
  "Backup ready"**.
- Server idle with `lastOutcome: "Failed"` shows a **failure line and not "Backup ready"**.

Then: `started` + `progress` renders the percent line; `done: completed` renders the save path;
`done: interrupted` renders failure and not success; the anchor carries `download`. Plus a browser
regression guard - with no `window.diariz`, the existing copy is unchanged.

For "must not show success" assertions, flush a macrotask and assert synchronously rather than
`waitFor`-ing a negative: `waitFor` checks once immediately, so a negative assertion passes before
the excluded thing could ever have arrived.

### `tests/Diariz.Api.Tests`

- `BackupProgress`: scope disposed after `Succeeded()` -> `Completed`; disposed without -> `Failed`.
- Overlapping builds: the first one failing must not stamp `Failed` while the second is still
  running, mirroring the care already taken over resetting counters.
- Controller: a throwing `IDatabaseBackup` fake (added to `Diariz.Api.TestSupport` - no mocking
  library in this repo) leaves `LastOutcome == Failed`.
- One test asserting `LastOutcome` reaches the client as the camelCase value the web actually reads,
  asserted on the response serialized the way ASP.NET does it. An enum crossing that boundary is
  exactly where casing surprises land.

### Mutation verification

Each test is checked by breaking the thing it covers and confirming the specific failure:

- Restore `setBackupReady(true)` -> the transfer tests must fail.
- No-op `Succeeded()` -> the `Completed` assertion must fail.
- Move the 5s threshold -> the noise-rule test must fail.

Quote the real failure output rather than asserting the tests went red. Edit files in place: restoring
a `.cs` from a copy preserves its mtime and MSBuild will keep testing the old binary.

### Not covered

The actual `will-download` wiring in `main.js`. The desktop harness is `node --test` with no Electron,
which is why the copy and arithmetic live in `downloadState.js` at all. That wiring needs a manual
check on a packaged build.

## Deployment surface

**Needs a desktop release.** This touches `apps/desktop/src/**`, so shipping it requires a `v*` tag
for the Windows installer plus the hand-built macOS `.dmg`. The web and API halves ship on a normal
server redeploy, but the progress reporting only reaches users once they install the new build.

## Release checklist

Version `0.244.1` -> **`0.245.0`** (functional enhancement: Minor +1, Build 0).

1. `version.json` plus its four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
   `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
2. `RELEASES[0]` entry in `apps/web/src/lib/releases.ts`.
3. `CAPABILITIES` - **no change**. Better feedback on an existing capability, not a new one.
4. README Features row - **update**. Line 58 currently claims "live progress while the archive is
   built or applied", which becomes incomplete.
5. `docs/features.md` bullet (line ~810) - **update in lockstep with the README row**.
6. `docs/Overall_Synopsis_of_Platform.md` - **update**. A new desktop<->web IPC channel is a
   cross-boundary contract.
7. `docs/Data_Schema.md` - **no change**. No schema change.

No OpenAPI snapshot regeneration and no n8n node regeneration: `api/maintenance` never reaches the
published document.

Per the fixes rule, open a GitHub issue for the false-success bug before writing the fix, and close it
from the PR body with `Fixes #<n>`.

## Deliberately out of scope

- Streaming the archive to the client as it is built (removing the temp-file stage). A much larger
  change to `MaintenanceController`, blocked by `ZipArchive` writing its central directory
  synchronously while Kestrel forbids synchronous response-body IO. The temp file is load-bearing.
- A tray menu item showing live download progress. Offered and declined; the panel plus a completion
  notification covers the need without another moving part.
- Progress for the *restore* upload half, which already has its own reporting.
