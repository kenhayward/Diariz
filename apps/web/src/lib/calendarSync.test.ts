import { describe, it, expect, vi } from "vitest";
import { elapsedSeconds, syncStatusKey, runCalendarSync, type CalendarSyncDeps } from "./calendarSync";

/// A fake desktop shell whose sync phase the test drives, matching the real one: it pushes state changes and
/// replays nothing on subscribe, so only `emit` can tell the run that the shell has finished.
function fakeShell(started: { started: boolean; reason?: string } = { started: true }) {
  const listeners: ((s: { phase: string }) => void)[] = [];
  return {
    syncOutlookNow: vi.fn().mockResolvedValue(started),
    onOutlookState: (cb: (s: { phase: string }) => void) => {
      listeners.push(cb);
      return () => listeners.splice(listeners.indexOf(cb), 1);
    },
    emit: (phase: string) => listeners.forEach((cb) => cb({ phase })),
    get listenerCount() {
      return listeners.length;
    },
  };
}

function deps(over: Partial<CalendarSyncDeps> = {}): CalendarSyncDeps {
  const shell = fakeShell();
  return {
    outlook: false,
    syncOutlookNow: shell.syncOutlookNow,
    onOutlookState: shell.onOutlookState,
    refetchEvents: vi.fn().mockResolvedValue(undefined),
    ...over,
  };
}

describe("elapsedSeconds", () => {
  it("counts whole seconds from the start", () => {
    expect(elapsedSeconds(1000, 1000)).toBe(0);
    expect(elapsedSeconds(1000, 1999)).toBe(0);
    expect(elapsedSeconds(1000, 24_000)).toBe(23);
  });

  // A clock that goes backwards (a system time change mid-sync) must not put a negative count in the bar.
  it("never goes negative", () => {
    expect(elapsedSeconds(5000, 1000)).toBe(0);
  });
});

describe("syncStatusKey", () => {
  it("names the scope, so the bar says which sync is running", () => {
    expect(syncStatusKey("all")).toBe("statusSyncingCalendar");
    expect(syncStatusKey("today")).toBe("statusSyncingCalendarToday");
  });
});

describe("runCalendarSync", () => {
  // Google and the .ics feeds are read live by the server on every events request, and it skips whichever the
  // user has not connected. So one refetch IS their refresh - there is nothing per-provider to trigger here.
  it("refreshes the calendar with no desktop Outlook in the picture", async () => {
    const d = deps();
    await runCalendarSync("all", d);

    expect(d.refetchEvents).toHaveBeenCalledTimes(1);
    expect(d.syncOutlookNow).not.toHaveBeenCalled();
  });

  it("passes the scope to the shell so a quick sync reads only today", async () => {
    const shell = fakeShell();
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const run = runCalendarSync("today", d);
    await vi.waitFor(() => expect(shell.syncOutlookNow).toHaveBeenCalledWith({ scope: "today" }));
    shell.emit("reading");
    shell.emit("idle");
    await run;
  });

  // The refetch has to wait for the shell: the harvested window is only on the server once the renderer has
  // POSTed it and the shell has dropped back to idle. Refetching before that redraws the meetings already on
  // screen and leaves the new one invisible - the exact bug the Calendar tab's own listener was written for.
  it("waits for the shell to finish before refreshing", async () => {
    const shell = fakeShell();
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const run = runCalendarSync("all", d);
    await vi.waitFor(() => expect(shell.syncOutlookNow).toHaveBeenCalled());
    shell.emit("reading");
    shell.emit("pushing");
    expect(d.refetchEvents).not.toHaveBeenCalled();

    shell.emit("idle");
    await run;
    expect(d.refetchEvents).toHaveBeenCalledTimes(1);
  });

  it("stops listening to the shell once the run is over", async () => {
    const shell = fakeShell();
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const run = runCalendarSync("all", d);
    await vi.waitFor(() => expect(shell.listenerCount).toBe(1));
    shell.emit("reading");
    shell.emit("idle");
    await run;

    expect(shell.listenerCount).toBe(0);
  });

  // A shell that refuses still leaves Google and the feeds to refresh, so the run continues and reports why.
  it("still refreshes when the shell refuses, and says why", async () => {
    const shell = fakeShell({ started: false, reason: "cooldown" });
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const result = await runCalendarSync("all", d);

    expect(d.refetchEvents).toHaveBeenCalledTimes(1);
    expect(result.outlookReason).toBe("cooldown");
  });

  // Without this, a shell that dies mid-read (or an older one that never reports idle) would leave the status
  // bar counting up for ever, with no way back except a reload.
  it("gives up on a shell that never comes back", async () => {
    vi.useFakeTimers();
    try {
      const shell = fakeShell();
      const d = deps({
        outlook: true,
        syncOutlookNow: shell.syncOutlookNow,
        onOutlookState: shell.onOutlookState,
        timeoutMs: 1000,
      });

      const run = runCalendarSync("all", d);
      await vi.advanceTimersByTimeAsync(1001);
      const result = await run;

      expect(result.outlookReason).toBe("timeout");
      expect(d.refetchEvents).toHaveBeenCalledTimes(1);
      expect(shell.listenerCount).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});
