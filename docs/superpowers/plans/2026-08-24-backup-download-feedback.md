# Backup Download Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a desktop backup download report its progress and its outcome, so an admin can tell how far it has got and whether it succeeded.

**Architecture:** The Electron shell gains a generic `will-download` handler that forwards raw byte counts and the final state to the renderer over one IPC channel, and raises an OS notification for downloads worth announcing. The Maintenance panel turns those raw events into user-facing progress. Separately, `BackupProgress` gains a commit-or-rollback outcome so a build that threw stops reporting success.

**Tech Stack:** Electron (CommonJS, `node --test`), React 19 + TypeScript + vitest/RTL, ASP.NET Core 10 + xUnit.

## Global Constraints

- **No em/en dashes in user-facing text.** Plain hyphen `-` only, in UI strings and all four locale catalogs (`en`, `fr`, `es`, `de`). Note `updateState.js` contains a pre-existing em dash; do not copy that style.
- **No mocking library** in the .NET tests. Add fakes to `tests/Diariz.Api.TestSupport`.
- **No jest-dom** in `apps/web`. Plain assertions (`expect(x).toBeTruthy()`, `.toBeNull()`).
- **TDD**: failing test first, watch it fail, then the minimal code.
- **Version:** `0.244.1` -> `0.245.0` (functional enhancement: Minor +1, Build 0).
- **Never `git add -A`** in this repo. Stage explicit paths.
- **Branch:** `feat/backup-download-feedback` (already created; spec already committed).

## Deviation from the spec (deliberate)

The spec put `downloadProgress({received, total})` in `apps/desktop/src/downloadState.js`. Implement it in the **web** instead:

- `apps/web/src/lib/format.ts` already exports `formatBytes`. Putting size formatting in the shell too would be the same value derived twice in two languages, drifting apart with nothing to catch it.
- Progress copy must go through i18n, which only the web has.
- The shell has no consumer for a percentage: the tray progress item was declined.

So the shell sends **raw bytes only**, `downloadState.js` holds **only** `notificationForDownload`, and all progress arithmetic and copy live in the panel.

## File Structure

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Services/BackupProgress.cs` (modify) | Add `BackupOutcome`, `IBackupScope.Succeeded()`, `LastOutcome` on the snapshot |
| `src/Diariz.Api/Controllers/MaintenanceController.cs` (modify) | Call `Succeeded()` before returning the file |
| `tests/Diariz.Api.TestSupport/Fakes.cs` (modify) | `FakeDatabaseBackup.DumpFailure` |
| `tests/Diariz.Api.Tests/BackupProgressTests.cs` (modify) | Outcome tests |
| `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs` (modify) | Controller outcome tests |
| `tests/Diariz.Api.Tests/BackupStatusSerializationTests.cs` (create) | `lastOutcome` wire format |
| `apps/desktop/src/downloadState.js` (create) | Pure notification decision |
| `apps/desktop/src/downloadState.test.js` (create) | Its tests |
| `apps/desktop/src/main.js` (modify) | `will-download` handler |
| `apps/desktop/src/preload.js` (modify) | `onDownloadEvent` |
| `apps/web/src/lib/desktopDownloads.ts` (create) | Bridge + `downloadProgress` arithmetic |
| `apps/web/src/lib/desktopDownloads.test.ts` (create) | Its tests |
| `apps/web/src/diariz.d.ts` (modify) | `onDownloadEvent` typing |
| `apps/web/src/lib/types.ts` (modify) | `BackupStatus.lastOutcome` |
| `apps/web/src/components/MaintenancePanel.tsx` (modify) | Transfer states, `download` attribute |
| `apps/web/src/components/MaintenancePanel.test.tsx` (modify) | Panel tests |
| `apps/web/src/locales/{en,fr,es,de}/account.json` (modify) | New strings |

---

### Task 0: Open the GitHub issue

CLAUDE.md requires every fix to start as a GitHub issue, closed by the PR body. The false-success bug is a fix; the progress reporting is an enhancement that rides along.

- [ ] **Step 1: Open the issue**

```bash
gh issue create --title "Backup reports success when the archive build failed" --body "$(cat <<'EOF'
**Symptom**

In Settings -> Maintenance, clicking "Download backup" shows a green "Backup ready - check your browser's downloads" even when the server failed to build the archive.

**Reproduce**

1. Sign in as a Platform Administrator.
2. Make the database dump fail (e.g. stop Postgres, or make `pg_dump` unavailable to the API container).
3. Settings -> Maintenance -> Download backup.

**Expected:** the panel says the backup could not be created.

**Actual:** the panel shows the green "Backup ready" message. No file is produced, so a scheduled backup can appear to have worked when it did not.

**Notes**

`MaintenancePanel.tsx` flips to `backupReady` whenever the server's progress goes from running to idle, which a thrown build does exactly as a successful one does. The download anchor also has no `download` attribute, so the 500 response navigates the desktop app window away to the error body.
EOF
)"
```

Record the issue number - the PR number is usually issue + 1, but confirm rather than assume; it feeds the `pr:` field in `releases.ts`.

---

### Task 1: Server records whether the build succeeded

**Files:**
- Modify: `src/Diariz.Api/Services/BackupProgress.cs`
- Modify: `src/Diariz.Api/Controllers/MaintenanceController.cs:73` and around `:110`
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs:769-789`
- Modify: `tests/Diariz.Api.Tests/BackupProgressTests.cs`
- Modify: `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs`
- Create: `tests/Diariz.Api.Tests/BackupStatusSerializationTests.cs`

**Interfaces:**
- Produces: `enum BackupOutcome { Completed, Failed }`; `interface IBackupScope : IDisposable { void Succeeded(); }`; `IBackupProgress.Begin()` now returns `IBackupScope`; `BackupProgressSnapshot(bool Running, BackupPhase? Phase, int ObjectsArchived, DateTimeOffset? StartedAt, BackupOutcome? LastOutcome)`.

- [ ] **Step 1: Write the failing tests in `BackupProgressTests.cs`**

Append inside the existing class:

```csharp
    [Fact]
    public void Current_BeforeAnyBuild_ReportsNoOutcome()
    {
        Assert.Null(new BackupProgress().Current.LastOutcome);
    }

    [Fact]
    public void AScopeDisposedAfterSucceeded_ReportsCompleted()
    {
        var progress = new BackupProgress();

        using (var scope = progress.Begin()) scope.Succeeded();

        Assert.Equal(BackupOutcome.Completed, progress.Current.LastOutcome);
    }

    [Fact]
    public void AScopeDisposedWithoutSucceeded_ReportsFailed()
    {
        // A build that threw unwinds through the using without ever committing, which is the whole point:
        // the panel used to read "went from running to idle" as success.
        var progress = new BackupProgress();

        progress.Begin().Dispose();

        Assert.Equal(BackupOutcome.Failed, progress.Current.LastOutcome);
    }

    [Fact]
    public void AFailedBuild_PublishesNoOutcomeWhileAnotherIsStillRunning()
    {
        // Two admins downloading at once: the first one failing must not be read as the second one's verdict.
        var progress = new BackupProgress();
        var first = progress.Begin();
        var second = progress.Begin();

        first.Dispose();
        Assert.Null(progress.Current.LastOutcome);

        second.Succeeded();
        second.Dispose();
        Assert.Equal(BackupOutcome.Completed, progress.Current.LastOutcome);
    }

    [Fact]
    public void StartingABuild_ClearsThePreviousOutcome()
    {
        // Otherwise a new build reports the last one's verdict before it has reached one of its own.
        var progress = new BackupProgress();
        progress.Begin().Dispose();

        using var second = progress.Begin();

        Assert.Null(progress.Current.LastOutcome);
    }
```

- [ ] **Step 2: Run them and confirm they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~BackupProgressTests"
```

Expected: compile errors - `BackupOutcome` does not exist, `LastOutcome` is not a member, `IDisposable` has no `Succeeded`.

- [ ] **Step 3: Implement in `BackupProgress.cs`**

Add above `BackupProgressSnapshot`:

```csharp
/// <summary>How the last finished build ended. Distinguishes "the archive is built" from "the build threw",
/// which the running/idle flag alone cannot: both leave the tracker idle.</summary>
public enum BackupOutcome
{
    Completed,
    Failed,
}
```

Replace the snapshot record with:

```csharp
/// <summary>A point-in-time view of the backup build, as returned by <c>GET api/maintenance/backup/status</c>.
/// <paramref name="Phase"/> is null when nothing is running. <paramref name="LastOutcome"/> is how the last
/// finished build ended, and is null while one is running and before any has run.</summary>
public record BackupProgressSnapshot(
    bool Running, BackupPhase? Phase, int ObjectsArchived, DateTimeOffset? StartedAt,
    BackupOutcome? LastOutcome);

/// <summary>One tracked build. Commit-or-rollback: a scope disposed without <see cref="Succeeded"/> is
/// recorded as a failure, so an exception unwinding through the <c>using</c> reports itself.</summary>
public interface IBackupScope : IDisposable
{
    /// <summary>Marks this build as having produced a complete archive.</summary>
    void Succeeded();
}
```

In `IBackupProgress`, change the `Begin` signature and doc:

```csharp
    /// <summary>Marks a build as running until the returned scope is disposed. Starting a build resets the
    /// phase, object count and outcome, so each archive reports its own progress from zero. Call
    /// <see cref="IBackupScope.Succeeded"/> before disposing, or the build is recorded as failed.</summary>
    IBackupScope Begin();
```

In `BackupProgress`, add the field, extend `Current`, reset in `Begin`, and rewrite `End`/`Scope`:

```csharp
    private BackupOutcome? _lastOutcome;
```

```csharp
                return _active > 0
                    ? new BackupProgressSnapshot(true, _phase, _objectsArchived, _startedAt, _lastOutcome)
                    : new BackupProgressSnapshot(false, null, 0, null, _lastOutcome);
```

```csharp
    public IBackupScope Begin()
    {
        lock (_gate)
        {
            // Only the outermost build resets the counters: two admins can download at once, and the first to
            // finish must not zero (or end) the other's progress.
            if (_active++ == 0)
            {
                _phase = BackupPhase.Database;
                _objectsArchived = 0;
                _startedAt = DateTimeOffset.UtcNow;
                _lastOutcome = null;
            }
        }
        return new Scope(this);
    }
```

```csharp
    private void End(bool succeeded)
    {
        lock (_gate)
        {
            if (_active == 0) return;
            _active--;
            // Only the last build in flight publishes a verdict. With one still running there is no settled
            // outcome to report, and stamping one would attribute this build's failure to that one.
            if (_active == 0) _lastOutcome = succeeded ? BackupOutcome.Completed : BackupOutcome.Failed;
        }
    }

    /// <summary>Ends one build on dispose, recording failure unless <see cref="Succeeded"/> was called.
    /// Idempotent, so a double-dispose can't end someone else's build.</summary>
    private sealed class Scope(BackupProgress owner) : IBackupScope
    {
        private bool _ended;
        private bool _succeeded;

        public void Succeeded() => _succeeded = true;

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            owner.End(_succeeded);
        }
    }
```

- [ ] **Step 4: Run and confirm they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~BackupProgressTests"
```

Expected: PASS.

- [ ] **Step 5: Write the failing controller tests**

Add to `tests/Diariz.Api.Tests/MaintenanceControllerTests.cs`:

```csharp
    [Fact]
    public async Task Backup_WhenTheArchiveIsBuilt_RecordsCompleted()
    {
        var progress = new BackupProgress();

        await Build(new FakeAudioStorage(), new FakeDatabaseBackup(), progress: progress).Backup();

        Assert.Equal(BackupOutcome.Completed, progress.Current.LastOutcome);
    }

    [Fact]
    public async Task Backup_WhenTheDumpFails_RecordsFailed()
    {
        // The panel reads the running->idle transition as "archive built"; without an outcome a thrown build
        // is indistinguishable from a good one and reports success.
        var progress = new BackupProgress();
        var backup = new FakeDatabaseBackup { DumpFailure = new InvalidOperationException("pg_dump died") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(new FakeAudioStorage(), backup, progress: progress).Backup());

        Assert.Equal(BackupOutcome.Failed, progress.Current.LastOutcome);
        Assert.False(progress.Current.Running);
    }
```

- [ ] **Step 6: Run and confirm they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceControllerTests"
```

Expected: compile error - `FakeDatabaseBackup` has no `DumpFailure`.

- [ ] **Step 7: Add `DumpFailure` to the fake**

In `tests/Diariz.Api.TestSupport/Fakes.cs`, in `FakeDatabaseBackup`:

```csharp
    /// <summary>Thrown from <see cref="DumpToAsync"/> when set, to exercise a build that dies mid-archive.</summary>
    public Exception? DumpFailure { get; set; }
```

and at the top of `DumpToAsync`, after `DumpCalled = true;`:

```csharp
        if (DumpFailure is not null) throw DumpFailure;
```

- [ ] **Step 8: Run and confirm `Completed` passes but `Failed` still fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceControllerTests"
```

Expected: `Backup_WhenTheDumpFails_RecordsFailed` PASSES already (the scope never commits), and `Backup_WhenTheArchiveIsBuilt_RecordsCompleted` FAILS with `Expected: Completed, Actual: Failed` - nothing calls `Succeeded()` yet.

- [ ] **Step 9: Call `Succeeded()` in the controller**

In `MaintenanceController.Backup`, immediately before the `return File(...)` line:

```csharp
        // Commit the build: the tracker records a failure for any scope disposed without this, so an
        // exception unwinding out of the archive assembly reports itself without a catch block.
        tracked.Succeeded();
```

- [ ] **Step 10: Run and confirm both pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceControllerTests"
```

Expected: PASS.

- [ ] **Step 11: Write the wire-format test**

Create `tests/Diariz.Api.Tests/BackupStatusSerializationTests.cs`:

```csharp
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The wire format of the backup status. The web reads `lastOutcome` as a string union, matching the
/// existing `phase` field; controller tests assert on the returned record and never see the JSON, which is
/// where casing and enum-shape surprises hide.</summary>
public class BackupStatusSerializationTests
{
    private static JsonSerializerOptions Options()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonConfig.Apply(o);
        return o;
    }

    [Fact]
    public void Snapshot_SerializesLastOutcomeAsACamelCaseStringName()
    {
        var snapshot = new BackupProgressSnapshot(false, null, 0, null, BackupOutcome.Failed);

        var json = JsonSerializer.Serialize(snapshot, Options());

        Assert.Contains("\"lastOutcome\":\"Failed\"", json);
    }

    [Fact]
    public void Snapshot_SerializesNoOutcomeAsNull()
    {
        var snapshot = new BackupProgressSnapshot(true, BackupPhase.Objects, 3, null, null);

        var json = JsonSerializer.Serialize(snapshot, Options());

        Assert.Contains("\"lastOutcome\":null", json);
    }
}
```

- [ ] **Step 12: Run the whole unit suite**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: PASS, with no warnings.

- [ ] **Step 13: Build the solution so integration/CodeQL compile breaks surface now**

```bash
dotnet build Diariz.slnx
```

Expected: build succeeded. (Controller constructor signatures have a second construction site in `RbacIntegrationTests.cs`; this catches it.)

- [ ] **Step 14: Mutation-verify**

Comment out `tracked.Succeeded();` **in place** (do not restore from a copy - a preserved mtime makes MSBuild skip the rebuild and you keep testing the old binary), re-run:

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MaintenanceControllerTests"
```

Confirm `Backup_WhenTheArchiveIsBuilt_RecordsCompleted` fails, then restore the line and re-run to green.

- [ ] **Step 15: Commit**

```bash
git add src/Diariz.Api/Services/BackupProgress.cs src/Diariz.Api/Controllers/MaintenanceController.cs tests/Diariz.Api.TestSupport/Fakes.cs tests/Diariz.Api.Tests/BackupProgressTests.cs tests/Diariz.Api.Tests/MaintenanceControllerTests.cs tests/Diariz.Api.Tests/BackupStatusSerializationTests.cs
git commit -m "fix(api): record whether a backup build succeeded

A scope disposed without Succeeded() is now recorded as a failure, so a
build that threw stops looking identical to one that worked."
```

---

### Task 2: Desktop notification model

**Files:**
- Create: `apps/desktop/src/downloadState.js`
- Create: `apps/desktop/src/downloadState.test.js`

**Interfaces:**
- Produces: `notificationForDownload(state, { filename, elapsedMs })` -> `{ title, body }` or `null`. `state` is Electron's done-state: `"completed" | "cancelled" | "interrupted"`.

- [ ] **Step 1: Write the failing test**

Create `apps/desktop/src/downloadState.test.js`:

```javascript
"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { notificationForDownload } = require("./downloadState");

test("a long download that completed announces where it went", () => {
  assert.deepEqual(
    notificationForDownload("completed", { filename: "diariz-backup.zip", elapsedMs: 30_000 }),
    { title: "Diariz", body: "Saved diariz-backup.zip" },
  );
});

test("a quick download stays silent", () => {
  // The handler covers every download in the app. A 5 KB transcript finishing does not need an OS
  // notification - you are still looking at the window that started it.
  assert.equal(
    notificationForDownload("completed", { filename: "transcript.md", elapsedMs: 1200 }),
    null,
  );
});

test("a failed download always announces itself, however quick", () => {
  // Failures are the case you must not miss, and they tend to fail fast.
  assert.deepEqual(
    notificationForDownload("interrupted", { filename: "diariz-backup.zip", elapsedMs: 200 }),
    { title: "Diariz", body: "Download failed - diariz-backup.zip" },
  );
});

test("a download the user cancelled stays silent", () => {
  assert.equal(
    notificationForDownload("cancelled", { filename: "diariz-backup.zip", elapsedMs: 30_000 }),
    null,
  );
});

test("an unknown state raises nothing", () => {
  assert.equal(notificationForDownload("weird", { filename: "x.zip", elapsedMs: 30_000 }), null);
  assert.equal(notificationForDownload("completed", undefined), null);
});
```

- [ ] **Step 2: Run it and confirm it fails**

```bash
cd apps/desktop && npm test
```

Expected: FAIL - `Cannot find module './downloadState'`.

- [ ] **Step 3: Implement**

Create `apps/desktop/src/downloadState.js`:

```javascript
"use strict";

// Pure model for the download handler's user-facing bit. `main.js` owns the Electron `will-download`
// wiring; the notification decision lives here so it can be unit-tested without a packaged build.
//
// Progress copy is deliberately NOT here. The handler forwards raw byte counts and the web app turns
// them into text: it already has `formatBytes` and the locale catalogs, and a second size formatter
// in a second language would only drift.

/// How long a download must run before finishing is worth an OS notification. Below this you are still
/// looking at the window that started it, and the handler covers every download in the app - a 5 KB
/// transcript announcing itself would be worse than the silence this replaces.
const NOTIFY_AFTER_MS = 5000;

/// What native notification (if any) a finished download should raise.
/// `state`: Electron's done-state, "completed" | "cancelled" | "interrupted".
/// Returns { title, body } or null.
function notificationForDownload(state, opts = {}) {
  const { filename, elapsedMs } = opts || {};
  if (!filename) return null;
  switch (state) {
    case "completed":
      return elapsedMs > NOTIFY_AFTER_MS ? { title: "Diariz", body: `Saved ${filename}` } : null;
    // A failure is the case you must not miss, and it tends to fail fast - so it ignores the threshold.
    case "interrupted":
      return { title: "Diariz", body: `Download failed - ${filename}` };
    // Cancelling is the user's own doing; telling them about it is noise.
    case "cancelled":
    default:
      return null;
  }
}

module.exports = { notificationForDownload, NOTIFY_AFTER_MS };
```

- [ ] **Step 4: Run and confirm it passes**

```bash
cd apps/desktop && npm test
```

Expected: PASS (all files, no failures).

- [ ] **Step 5: Mutation-verify the noise rule**

Change `NOTIFY_AFTER_MS` to `0` in place, re-run `npm test`, confirm "a quick download stays silent" fails with the actual object printed against `null`. Restore to `5000`, re-run to green.

- [ ] **Step 6: Commit**

```bash
git add apps/desktop/src/downloadState.js apps/desktop/src/downloadState.test.js
git commit -m "feat(desktop): notification model for finished downloads"
```

---

### Task 3: Wire `will-download` in the shell

**Files:**
- Modify: `apps/desktop/src/main.js` (in `createWindow`, after the `setDisplayMediaRequestHandler` block ending around `:131`)
- Modify: `apps/desktop/src/preload.js`

**Interfaces:**
- Consumes: `notificationForDownload` from Task 2.
- Produces: IPC channel `download:event`, payload
  `{ type: "started"|"progress"|"done", id: number, url: string, filename: string, totalBytes: number, receivedBytes: number, state?: string, savePath?: string }`;
  preload method `onDownloadEvent(cb) => () => void`.

There is no automated test for this step: the desktop harness is `node --test` with no Electron. That is why the decision logic lives in Task 2. This step is verified manually in Task 6.

- [ ] **Step 1: Require the model in `main.js`**

Beside the existing `require("./updateState")` line (around `:24`):

```javascript
const { notificationForDownload } = require("./downloadState");
```

- [ ] **Step 2: Register the handler**

In `createWindow`, immediately after the `setDisplayMediaRequestHandler(...)` call closes:

```javascript
  // The shell replaced the browser, and with it the download shelf: without this, a download has no
  // progress, no completion notice and no visible failure - a multi-GB platform backup just goes quiet.
  // Deliberately does NOT call item.setSavePath, so Electron's Save-As dialog stays: a backup carries every
  // password hash on the platform and should land where the admin chose.
  //
  // Raw byte counts only. The renderer owns the arithmetic and the wording, where formatBytes and the
  // locale catalogs already live.
  let downloadSeq = 0;
  mainWindow.webContents.session.on("will-download", (_event, item) => {
    const id = ++downloadSeq;
    const url = item.getURL();
    const filename = item.getFilename();
    const startedAt = Date.now();
    const send = (payload) => {
      if (!mainWindow || mainWindow.isDestroyed()) return;
      mainWindow.webContents.send("download:event", { id, url, filename, ...payload });
    };

    send({ type: "started", totalBytes: item.getTotalBytes(), receivedBytes: 0 });
    item.on("updated", () => {
      send({
        type: "progress",
        totalBytes: item.getTotalBytes(),
        receivedBytes: item.getReceivedBytes(),
      });
    });
    item.once("done", (_doneEvent, state) => {
      send({
        type: "done",
        state,
        savePath: item.getSavePath(),
        totalBytes: item.getTotalBytes(),
        receivedBytes: item.getReceivedBytes(),
      });
      const note = notificationForDownload(state, { filename, elapsedMs: Date.now() - startedAt });
      if (note && Notification.isSupported()) new Notification(note).show();
    });
  });
```

- [ ] **Step 3: Expose it in `preload.js`**

Append inside the `exposeInMainWorld("diariz", { ... })` object, after the Outlook block:

```javascript
  // ---- Downloads ----

  /// Subscribe to download lifecycle events for this window's session. `cb` receives
  /// { type: "started"|"progress"|"done", id, url, filename, receivedBytes, totalBytes, state?, savePath? }.
  /// Raw byte counts - the web app formats them. Returns an unsubscribe function.
  onDownloadEvent: (cb) => {
    const listener = (_event, payload) => cb(payload);
    ipcRenderer.on("download:event", listener);
    return () => ipcRenderer.removeListener("download:event", listener);
  },
```

- [ ] **Step 4: Confirm the desktop suite still passes**

```bash
cd apps/desktop && npm test
```

Expected: PASS. (`main.js`/`preload.js` are not under test; this confirms nothing else broke.)

- [ ] **Step 5: Commit**

```bash
git add apps/desktop/src/main.js apps/desktop/src/preload.js
git commit -m "feat(desktop): report download progress and completion to the renderer"
```

---

### Task 4: Web bridge and progress arithmetic

**Files:**
- Create: `apps/web/src/lib/desktopDownloads.ts`
- Create: `apps/web/src/lib/desktopDownloads.test.ts`
- Modify: `apps/web/src/diariz.d.ts`

**Interfaces:**
- Consumes: the `download:event` payload from Task 3.
- Produces:
  - `type DesktopDownloadState = "completed" | "cancelled" | "interrupted"`
  - `interface DesktopDownloadEvent { type: "started" | "progress" | "done"; id: number; url: string; filename: string; receivedBytes: number; totalBytes: number; state?: DesktopDownloadState; savePath?: string }`
  - `interface DesktopDownloadBridge { onDownloadEvent?: (cb: (e: DesktopDownloadEvent) => void) => () => void }`
  - `watchDesktopDownloads(bridge, matchUrl, onEvent) => () => void`
  - `downloadProgress({ receivedBytes, totalBytes }) => { phase: "starting" | "progressing"; percent: number | null; sizeText: string }`

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/desktopDownloads.test.ts`:

```typescript
import { describe, it, expect, vi } from "vitest";
import {
  watchDesktopDownloads,
  downloadProgress,
  type DesktopDownloadEvent,
} from "./desktopDownloads";

/// A fake of the `window.diariz` bridge the Electron preload injects.
function fakeBridge() {
  const listeners: ((e: DesktopDownloadEvent) => void)[] = [];
  return {
    listeners,
    emit(e: DesktopDownloadEvent) {
      for (const l of [...listeners]) l(e);
    },
    onDownloadEvent(cb: (e: DesktopDownloadEvent) => void) {
      listeners.push(cb);
      return () => {
        const i = listeners.indexOf(cb);
        if (i >= 0) listeners.splice(i, 1);
      };
    },
  };
}

const event = (over: Partial<DesktopDownloadEvent> = {}): DesktopDownloadEvent => ({
  type: "progress",
  id: 1,
  url: "https://host/api/maintenance/backup?access_token=t",
  filename: "diariz-backup.zip",
  receivedBytes: 1,
  totalBytes: 2,
  ...over,
});

describe("watchDesktopDownloads", () => {
  it("is a no-op without a desktop bridge", () => {
    const onEvent = vi.fn();

    // The web app runs this unconditionally, so a browser reaching for a missing bridge would break it.
    const dispose = watchDesktopDownloads(undefined, () => true, onEvent);
    dispose();

    expect(onEvent).not.toHaveBeenCalled();
  });

  it("delivers events whose url the caller claims", () => {
    const bridge = fakeBridge();
    const onEvent = vi.fn();
    watchDesktopDownloads(bridge, (url) => url.includes("/api/maintenance/backup"), onEvent);

    bridge.emit(event());

    expect(onEvent).toHaveBeenCalledTimes(1);
    expect(onEvent.mock.calls[0][0].filename).toBe("diariz-backup.zip");
  });

  it("ignores downloads belonging to something else", () => {
    // The shell handler is generic - audio and transcript downloads come down the same channel.
    const bridge = fakeBridge();
    const onEvent = vi.fn();
    watchDesktopDownloads(bridge, (url) => url.includes("/api/maintenance/backup"), onEvent);

    bridge.emit(event({ url: "https://host/api/recordings/1/audio", filename: "meeting.webm" }));

    expect(onEvent).not.toHaveBeenCalled();
  });

  it("stops delivering once disposed", () => {
    const bridge = fakeBridge();
    const onEvent = vi.fn();
    const dispose = watchDesktopDownloads(bridge, () => true, onEvent);

    dispose();
    bridge.emit(event());

    expect(onEvent).not.toHaveBeenCalled();
    expect(bridge.listeners.length).toBe(0);
  });
});

describe("downloadProgress", () => {
  it("reports starting until bytes arrive", () => {
    // will-download fires before the Save-As dialog resolves, so zero received is a real state.
    expect(downloadProgress({ receivedBytes: 0, totalBytes: 2048 }).phase).toBe("starting");
  });

  it("reports a percentage of the total when the total is known", () => {
    const p = downloadProgress({ receivedBytes: 512, totalBytes: 1024 });

    expect(p.phase).toBe("progressing");
    expect(p.percent).toBe(50);
    expect(p.sizeText).toBe("1 KB");
  });

  it("reports how much has arrived when the total is unknown", () => {
    // No Content-Length: there is no percentage to give, so say what has landed instead of showing 0%.
    const p = downloadProgress({ receivedBytes: 2048, totalBytes: 0 });

    expect(p.phase).toBe("progressing");
    expect(p.percent).toBeNull();
    expect(p.sizeText).toBe("2 KB");
  });

  it("clamps at 100 when the received count overshoots the total", () => {
    expect(downloadProgress({ receivedBytes: 1100, totalBytes: 1000 }).percent).toBe(100);
  });
});
```

- [ ] **Step 2: Run it and confirm it fails**

```bash
cd apps/web && npx vitest run src/lib/desktopDownloads.test.ts
```

Expected: FAIL - cannot resolve `./desktopDownloads`.

- [ ] **Step 3: Implement**

Create `apps/web/src/lib/desktopDownloads.ts`:

```typescript
// Bridge to the Electron shell's download handler. The desktop preload injects
// `window.diariz.onDownloadEvent`; in a plain browser it is absent and everything here degrades to a
// no-op, because the browser's own download shelf already does this job.
//
// The shell sends raw byte counts and leaves the arithmetic and the wording here, where `formatBytes`
// and the locale catalogs already are.

import { formatBytes } from "./format";

export type DesktopDownloadState = "completed" | "cancelled" | "interrupted";

export interface DesktopDownloadEvent {
  type: "started" | "progress" | "done";
  id: number;
  url: string;
  filename: string;
  receivedBytes: number;
  totalBytes: number;
  /// Present on "done" only.
  state?: DesktopDownloadState;
  /// Where the file landed. Present on "done" only, and empty when the user cancelled the Save dialog.
  savePath?: string;
}

export interface DesktopDownloadBridge {
  onDownloadEvent?: (cb: (e: DesktopDownloadEvent) => void) => () => void;
}

/// Subscribe to the downloads this caller cares about. The shell's handler is generic - audio, transcript
/// and formula-result downloads all arrive here - so `matchUrl` decides which are yours.
/// Returns a disposer, which is safe to call even with no bridge.
export function watchDesktopDownloads(
  bridge: DesktopDownloadBridge | undefined,
  matchUrl: (url: string) => boolean,
  onEvent: (e: DesktopDownloadEvent) => void,
): () => void {
  if (!bridge?.onDownloadEvent) return () => {};
  return bridge.onDownloadEvent((e) => {
    if (matchUrl(e.url)) onEvent(e);
  });
}

export interface DownloadProgress {
  phase: "starting" | "progressing";
  /// Null when the response carried no Content-Length, so there is no percentage to show.
  percent: number | null;
  /// The total when it is known, otherwise how much has arrived.
  sizeText: string;
}

/// Turn raw byte counts into the numbers a progress line needs. Derived from the counts rather than from
/// an assumed sequence of events: Electron fires will-download before the Save-As dialog resolves, so
/// "started but no bytes yet" can last as long as the user takes to pick a folder.
export function downloadProgress({
  receivedBytes,
  totalBytes,
}: {
  receivedBytes: number;
  totalBytes: number;
}): DownloadProgress {
  if (!(receivedBytes > 0)) {
    return { phase: "starting", percent: null, sizeText: formatBytes(Math.max(totalBytes, 0)) };
  }
  if (!(totalBytes > 0)) {
    return { phase: "progressing", percent: null, sizeText: formatBytes(receivedBytes) };
  }
  // Chromium can report a received count a shade over the total; 101% reads as a bug.
  const percent = Math.min(100, Math.round((receivedBytes / totalBytes) * 100));
  return { phase: "progressing", percent, sizeText: formatBytes(totalBytes) };
}
```

- [ ] **Step 4: Run and confirm it passes**

```bash
cd apps/web && npx vitest run src/lib/desktopDownloads.test.ts
```

Expected: PASS (9 tests).

- [ ] **Step 5: Add the typing**

Replace `apps/web/src/diariz.d.ts` with:

```typescript
import type { DesktopDownloadEvent } from "./lib/desktopDownloads";

export {};
declare global {
  interface Window {
    diariz?: {
      isElectron?: boolean;
      startGoogleSignIn?: () => void;
      onAuthToken?: (cb: (token: string) => void) => () => void;
      onAuthError?: (cb: (reason: string) => void) => () => void;
      onDownloadEvent?: (cb: (e: DesktopDownloadEvent) => void) => () => void;
    };
  }
}
```

- [ ] **Step 6: Typecheck**

```bash
cd apps/web && npm run build
```

Expected: succeeds.

- [ ] **Step 7: Mutation-verify the URL filter**

Change `if (matchUrl(e.url)) onEvent(e);` to `onEvent(e);` in place, re-run the test file, confirm "ignores downloads belonging to something else" fails. Restore and re-run to green.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/desktopDownloads.ts apps/web/src/lib/desktopDownloads.test.ts apps/web/src/diariz.d.ts
git commit -m "feat(web): desktop download bridge and progress arithmetic"
```

---

### Task 5: Panel reports the transfer and the outcome

**Files:**
- Modify: `apps/web/src/lib/types.ts:576-581`
- Modify: `apps/web/src/components/MaintenancePanel.tsx`
- Modify: `apps/web/src/components/MaintenancePanel.test.tsx`
- Modify: `apps/web/src/locales/{en,fr,es,de}/account.json`

**Interfaces:**
- Consumes: `watchDesktopDownloads`, `downloadProgress`, `DesktopDownloadEvent` from Task 4; `BackupStatus.lastOutcome` from Task 1.

- [ ] **Step 1: Add the new locale strings**

`apps/web/src/locales/en/account.json`, after `"backupReady"`:

```json
  "backupDownloadStarting": "Starting download - choose where to save it.",
  "backupDownloading": "Downloading backup - {{percent}}% of {{size}}",
  "backupDownloadingUnknown": "Downloading backup - {{size}} so far",
  "backupSavedTo": "Backup saved to {{path}}",
  "backupDownloadFailed": "The backup download did not finish. Try again.",
  "backupBuildFailed": "The backup could not be created. Check the server logs and try again.",
```

`fr/account.json`:

```json
  "backupDownloadStarting": "Telechargement en cours - choisissez ou l'enregistrer.",
  "backupDownloading": "Telechargement de la sauvegarde - {{percent}} % de {{size}}",
  "backupDownloadingUnknown": "Telechargement de la sauvegarde - {{size}} jusqu'ici",
  "backupSavedTo": "Sauvegarde enregistree dans {{path}}",
  "backupDownloadFailed": "Le telechargement de la sauvegarde ne s'est pas termine. Reessayez.",
  "backupBuildFailed": "Impossible de creer la sauvegarde. Consultez les journaux du serveur et reessayez.",
```

`es/account.json`:

```json
  "backupDownloadStarting": "Iniciando la descarga - elige donde guardarla.",
  "backupDownloading": "Descargando la copia de seguridad - {{percent}} % de {{size}}",
  "backupDownloadingUnknown": "Descargando la copia de seguridad - {{size}} hasta ahora",
  "backupSavedTo": "Copia de seguridad guardada en {{path}}",
  "backupDownloadFailed": "La descarga de la copia de seguridad no termino. Intentalo de nuevo.",
  "backupBuildFailed": "No se pudo crear la copia de seguridad. Revisa los registros del servidor e intentalo de nuevo.",
```

`de/account.json`:

```json
  "backupDownloadStarting": "Download wird gestartet - waehlen Sie einen Speicherort.",
  "backupDownloading": "Backup wird heruntergeladen - {{percent}} % von {{size}}",
  "backupDownloadingUnknown": "Backup wird heruntergeladen - bisher {{size}}",
  "backupSavedTo": "Backup gespeichert unter {{path}}",
  "backupDownloadFailed": "Der Backup-Download wurde nicht abgeschlossen. Versuchen Sie es erneut.",
  "backupBuildFailed": "Das Backup konnte nicht erstellt werden. Pruefen Sie die Serverprotokolle und versuchen Sie es erneut.",
```

Match the surrounding files' accent conventions when editing: if the existing entries use accented characters, use them here too.

- [ ] **Step 2: Extend `BackupStatus`**

In `apps/web/src/lib/types.ts`:

```typescript
export interface BackupStatus {
  running: boolean;
  phase: "Database" | "Objects" | null;
  objectsArchived: number;
  startedAt: string | null;
  /// How the last finished build ended. Null while one is running and before any has run. The panel used to
  /// read running -> idle as success, which a build that threw does exactly as a good one does.
  lastOutcome: "Completed" | "Failed" | null;
}
```

- [ ] **Step 3: Write the failing tests**

In `apps/web/src/components/MaintenancePanel.test.tsx`, extend the `idle` constant and add a bridge helper below it:

```typescript
const idle = { running: false, phase: null, objectsArchived: 0, startedAt: null, lastOutcome: null };
const built = { ...idle, lastOutcome: "Completed" as const };

/// Install a fake of the Electron shell's download bridge, and return an emitter for it.
function installDesktopBridge() {
  const listeners: ((e: unknown) => void)[] = [];
  (window as unknown as { diariz?: unknown }).diariz = {
    isElectron: true,
    onDownloadEvent: (cb: (e: unknown) => void) => {
      listeners.push(cb);
      return () => {
        const i = listeners.indexOf(cb);
        if (i >= 0) listeners.splice(i, 1);
      };
    },
  };
  return (e: Record<string, unknown>) => {
    for (const l of [...listeners]) {
      l({
        id: 1,
        url: "/api/maintenance/backup?access_token=t",
        filename: "diariz-backup.zip",
        receivedBytes: 0,
        totalBytes: 0,
        ...e,
      });
    }
  };
}
```

Add to the `afterEach` in `describe("MaintenancePanel backup")`:

```typescript
    delete (window as unknown as { diariz?: unknown }).diariz;
```

Then add these tests inside that describe block:

```typescript
  it("reports the transfer instead of claiming the backup is ready, in the desktop shell", async () => {
    // The shell has no download shelf: "check your browser's downloads" is unfollowable there, and the
    // archive build finishing is the START of a multi-GB transfer, not the end of the job.
    vi.useFakeTimers();
    const emit = installDesktopBridge();
    (api.backupStatus as Mock)
      .mockResolvedValueOnce({ ...built, running: true, phase: "Objects", objectsArchived: 3 })
      .mockResolvedValue(built);
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });
    await act(async () => {
      emit({ type: "started", totalBytes: 1024 });
      await vi.advanceTimersByTimeAsync(1600);
    });

    expect(screen.queryByText(/backup ready/i)).toBeNull();
    expect(screen.getByText(/starting download/i)).toBeTruthy();
  });

  it("shows the percentage as the archive transfers", async () => {
    const emit = installDesktopBridge();
    (api.backupStatus as Mock).mockResolvedValue(built);
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => { emit({ type: "progress", receivedBytes: 512, totalBytes: 1024 }); });

    expect(screen.getByText(/50% of 1 KB/i)).toBeTruthy();
  });

  it("says where the file landed once the transfer finishes", async () => {
    const emit = installDesktopBridge();
    (api.backupStatus as Mock).mockResolvedValue(built);
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => {
      emit({ type: "done", state: "completed", savePath: "C:\\Users\\me\\diariz-backup.zip",
             receivedBytes: 1024, totalBytes: 1024 });
    });

    expect(screen.getByText(/backup saved to/i)).toBeTruthy();
    expect(screen.getByText(/diariz-backup\.zip/i)).toBeTruthy();
  });

  it("reports an interrupted transfer as a failure, not a success", async () => {
    const emit = installDesktopBridge();
    (api.backupStatus as Mock).mockResolvedValue(built);
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => {
      emit({ type: "done", state: "interrupted", receivedBytes: 10, totalBytes: 1024 });
    });
    // Assert synchronously after a flushed tick: waitFor checks once immediately, so a "must not appear"
    // assertion inside it passes before the thing could ever have appeared.
    await act(async () => { await Promise.resolve(); });

    expect(screen.getByText(/did not finish/i)).toBeTruthy();
    expect(screen.queryByText(/backup ready/i)).toBeNull();
    expect(screen.queryByText(/backup saved to/i)).toBeNull();
  });

  it("returns to idle when the user cancels the save dialog", async () => {
    const emit = installDesktopBridge();
    (api.backupStatus as Mock).mockResolvedValue(built);
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => { emit({ type: "done", state: "cancelled", savePath: "" }); });
    await act(async () => { await Promise.resolve(); });

    expect(screen.queryByText(/did not finish/i)).toBeNull();
    expect(screen.queryByText(/backup saved to/i)).toBeNull();
  });

  it("reports a build that failed instead of showing the ready message", async () => {
    // A thrown build leaves the tracker idle exactly as a good one does; only the outcome tells them apart.
    vi.useFakeTimers();
    (api.backupStatus as Mock)
      .mockResolvedValueOnce({ ...idle, running: true, phase: "Database" })
      .mockResolvedValue({ ...idle, lastOutcome: "Failed" });
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });
    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });

    expect(screen.getByText(/could not be created/i)).toBeTruthy();
    expect(screen.queryByText(/backup ready/i)).toBeNull();
  });

  it("keeps the browser wording when there is no desktop shell", async () => {
    // No window.diariz: the browser's own download shelf still does this job, so nothing should change.
    vi.useFakeTimers();
    (api.backupStatus as Mock)
      .mockResolvedValueOnce({ ...built, running: true, phase: "Objects", objectsArchived: 1 })
      .mockResolvedValue(built);
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });
    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });

    expect(screen.getByText(/backup ready/i)).toBeTruthy();
  });

  it("marks the download link as a download so a failure cannot navigate the app away", async () => {
    // Without this a 500 response replaces the desktop window with the error body.
    render(<MaintenancePanel />);

    const link = screen.getByRole("link", { name: /download backup/i });

    expect(link.hasAttribute("download")).toBe(true);
  });
```

Leave the existing tests' mocks alone. Only `lastOutcome: "Failed"` means failure; `null` still reports
success, which keeps the panel honest against a server that has not been upgraded yet (the field would be
absent, and treating absent as failure would report a phantom failure for every good backup).

- [ ] **Step 4: Run and confirm they fail**

```bash
cd apps/web && npx vitest run src/components/MaintenancePanel.test.tsx
```

Expected: the new tests fail - no `download` attribute, no transfer or failure copy, and "backup ready" still shows on the desktop path.

- [ ] **Step 5: Implement in `MaintenancePanel.tsx`**

Add imports:

```typescript
import {
  watchDesktopDownloads,
  downloadProgress,
  type DesktopDownloadEvent,
} from "../lib/desktopDownloads";
```

Add the transfer state beside the other backup state:

```typescript
  // What the shell's download is doing. Null in a browser, where the download shelf already reports this.
  // A transfer event outranks the poll: the two are independent signals and can interleave either way at a
  // 1.5s poll interval, and only the transfer knows anything after the archive was built.
  const [transfer, setTransfer] = useState<DesktopDownloadEvent | null>(null);
  const [buildFailed, setBuildFailed] = useState(false);
```

Reset both in `watchBackup()`:

```typescript
    setTransfer(null);
    setBuildFailed(false);
```

Subscribe once on mount:

```typescript
  useEffect(
    () =>
      watchDesktopDownloads(
        window.diariz,
        (url) => url.includes("/api/maintenance/backup"),
        (e) => {
          // Cancelling the Save dialog is the user's own doing - go quiet rather than report it.
          setTransfer(e.type === "done" && e.state === "cancelled" ? null : e);
          setBackupWatching(false);
        },
      ),
    [],
  );
```

In the poll's idle branch, replace the success assumption:

```typescript
      if (sawBuildRunning.current) {
        setBackupWatching(false);
        if (status.lastOutcome === "Failed") setBuildFailed(true);
        else setBackupReady(true);
      } else if (Date.now() - backupStartedAt.current > BACKUP_GRACE_MS) {
```

Add `download` to the anchor:

```tsx
        <a
          href={api.backupUrl()}
          download
          onClick={watchBackup}
```

Replace the `{backupReady && ...}` line with the full report block:

```tsx
        {buildFailed && (
          <p role="status" className="text-xs text-red-600 dark:text-red-400">
            {t("backupBuildFailed")}
          </p>
        )}
        {transfer && (
          <p role="status" className="text-xs text-gray-600 dark:text-gray-300">
            {transferMessage(transfer, t)}
          </p>
        )}
        {backupReady && !transfer && (
          <p className="text-xs text-green-600 dark:text-green-400">{t("backupReady")}</p>
        )}
```

Add the message helper above the component:

```typescript
/// The one line describing where the shell's download has got to. Kept out of the component so the
/// event-shape-to-copy mapping is readable in one piece.
function transferMessage(e: DesktopDownloadEvent, t: (k: string, o?: object) => string): string {
  if (e.type === "done") {
    if (e.state === "completed") return t("backupSavedTo", { path: e.savePath });
    return t("backupDownloadFailed");
  }
  const p = downloadProgress(e);
  if (p.phase === "starting") return t("backupDownloadStarting");
  if (p.percent === null) return t("backupDownloadingUnknown", { size: p.sizeText });
  return t("backupDownloading", { percent: p.percent, size: p.sizeText });
}
```

- [ ] **Step 6: Run and confirm they pass**

```bash
cd apps/web && npx vitest run src/components/MaintenancePanel.test.tsx
```

Expected: PASS, including the pre-existing tests.

- [ ] **Step 7: Run the whole web suite and typecheck**

```bash
cd apps/web && npx vitest run && npm run build
```

Expected: PASS, no new warnings.

- [ ] **Step 8: Mutation-verify**

Restore `setBackupReady(true)` unconditionally in the idle branch, in place. Re-run the panel test file and confirm "reports a build that failed instead of showing the ready message" fails. Then remove the `!transfer` guard and confirm the desktop test fails. Restore both, re-run to green.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/MaintenancePanel.tsx apps/web/src/components/MaintenancePanel.test.tsx apps/web/src/locales/en/account.json apps/web/src/locales/fr/account.json apps/web/src/locales/es/account.json apps/web/src/locales/de/account.json
git commit -m "feat(web): report backup download progress and outcome"
```

---

### Task 6: Manual verification on a packaged desktop build

The `will-download` wiring has no automated coverage. Verify it by hand before the PR.

- [ ] **Step 1: Run the shell against a dev web server**

```bash
cd apps/desktop && npm run dev
```

- [ ] **Step 2: Exercise the happy path**

Sign in as a Platform Administrator, Settings -> Maintenance -> Download backup. Confirm, in order: build progress, the Save-As dialog, "Starting download", a rising percentage, "Backup saved to <path>", and - if the download ran over 5 seconds - an OS notification. Confirm the file exists at that path and opens as a zip.

- [ ] **Step 3: Exercise cancel**

Click Download backup, then cancel the Save-As dialog. The panel should go quiet, with no failure and no success claim.

- [ ] **Step 4: Exercise a failed build**

Stop Postgres (`docker compose stop postgres` in `deploy/`), click Download backup. The panel should show "The backup could not be created", and **the app window must still be the app** - not an error page. Restart Postgres afterwards.

- [ ] **Step 5: Record the results**

Note what was checked and anything that differed, for the PR body.

---

### Task 7: Release checklist and PR

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md:58`
- Modify: `docs/features.md` (~line 810)
- Modify: `docs/Overall_Synopsis_of_Platform.md`

- [ ] **Step 1: Bump the version to `0.245.0` in all five places**

`version.json` -> `{ "version": "0.245.0" }`; the `"version"` field in the three `package.json` files; `<Version>` in `Diariz.Api.csproj`.

- [ ] **Step 2: Add the release entry**

At the top of `RELEASES` in `apps/web/src/lib/releases.ts`. Use the real PR number - confirm it, do not assume issue + 1.

```typescript
  {
    version: "0.245.0",
    date: "2026-08-24",
    pr: <PR number>,
    headline: "See how a backup download is going, and whether it worked",
    summary:
      "Downloading a platform backup from the desktop app used to go silent the moment the archive was built - there was no download shelf, no progress and no completion notice, so there was no way to tell whether a multi-gigabyte transfer had finished or failed. The desktop app now reports the transfer as it runs, says where the file landed, and raises a notification when a long download finishes. A backup whose archive failed to build also stops reporting success: it now says so, instead of showing the same green message as a backup that worked.",
    added: [
      "Desktop: live download progress, the saved file's location, and a notification when a long download finishes - for backups and every other download in the app.",
    ],
    fixed: [
      "A backup whose archive failed to build reported success instead of the failure.",
      "A failed backup request could navigate the desktop app window away to the error page.",
    ],
  },
```

- [ ] **Step 3: Update the README Features row**

`README.md:58` currently ends "with live progress while the archive is built or applied". Change that clause to "with live progress while the archive is built, downloaded, or applied".

- [ ] **Step 4: Update `docs/features.md` in lockstep**

Extend the **Backup & restore** bullet (~line 810) with the download reporting: progress while the archive transfers, where the file was saved, a notification on a long download in the desktop app, and an explicit failure when the archive could not be built.

- [ ] **Step 5: Update `docs/Overall_Synopsis_of_Platform.md`**

Add the new cross-boundary contract to the desktop section: the shell's `will-download` handler forwards `download:event` (`started`/`progress`/`done`, raw byte counts) to the renderer, which owns the formatting and copy; the shell owns only the notification decision (`downloadState.js`).

- [ ] **Step 6: Verify the mirrors and release data**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: PASS.

- [ ] **Step 7: Full verification before the PR**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
cd apps/web && npx vitest run && npm run build
cd ../desktop && npm test
```

Expected: all PASS, output clean.

- [ ] **Step 8: Commit and open the PR**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md
git commit -m "chore: release 0.245.0"
git push -u origin feat/backup-download-feedback
```

Then `gh pr create`. The body must contain `Fixes #<issue from Task 0>` on its own line, and must state the deployment surface: **this needs a desktop release** (a `v*` tag for the Windows installer plus the hand-built macOS `.dmg`), because it touches `apps/desktop/src/**`. The web and API halves ship on a normal server redeploy.

- [ ] **Step 9: Confirm the issue closed after merge**

GitHub only auto-closes from the PR description. Check the issue, and close it by hand if it did not.
