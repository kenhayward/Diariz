import { describe, it, expect, vi } from "vitest";
import {
  elapsedSeconds,
  syncStatusKey,
  dayStartIso,
  syncErrorKey,
  runCalendarSync,
  type CalendarSyncDeps,
} from "./calendarSync";

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
    // The quick sync now reads whichever day is selected, so its message names that day rather than
    // claiming "today" - which was wrong every time the user was looking at any other date.
    expect(syncStatusKey("today")).toBe("statusSyncingCalendarDay");
  });
});

describe("dayStartIso", () => {
  // The status bar must name the same day the shell reads. `new Date("2026-08-20")` is UTC midnight, which
  // is the 19th locally anywhere west of Greenwich - so a naive parse would have the message and the sync
  // disagreeing by a day for every user in the Americas.
  it("reads a calendar key as a local date, not a UTC instant", () => {
    const d = new Date(dayStartIso("2026-08-20"));
    expect(d.getFullYear()).toBe(2026);
    expect(d.getMonth()).toBe(7);
    expect(d.getDate()).toBe(20);
    expect(d.getHours()).toBe(0);
  });

  // No selection means the shell syncs today, so the message has to say today too.
  it("falls back to today when there is no key, matching what the shell will read", () => {
    const today = new Date();
    for (const key of [undefined, "", "not-a-date"]) {
      const d = new Date(dayStartIso(key));
      expect(d.toDateString()).toBe(today.toDateString());
    }
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

  it("passes the scope to the shell so a quick sync reads only one day", async () => {
    const shell = fakeShell();
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const run = runCalendarSync("today", d);
    await vi.waitFor(() =>
      expect(shell.syncOutlookNow).toHaveBeenCalledWith({ scope: "today", date: undefined }),
    );
    shell.emit("reading");
    shell.emit("idle");
    await run;
  });

  // The whole point of the change: the button reads the day the user is looking at. Without the date the
  // shell falls back to today, which is what it always used to do.
  it("passes the selected day to the shell, so it reads that day and not today", async () => {
    const shell = fakeShell();
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const run = runCalendarSync("today", d, "2026-08-20");
    await vi.waitFor(() =>
      expect(shell.syncOutlookNow).toHaveBeenCalledWith({ scope: "today", date: "2026-08-20" }),
    );
    shell.emit("reading");
    shell.emit("idle");
    await run;
  });

  // A date on the full sync would be meaningless - that button reads the whole configured window.
  it("never sends a date with the full sync", async () => {
    const shell = fakeShell();
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const run = runCalendarSync("all", d, "2026-08-20");
    await vi.waitFor(() =>
      expect(shell.syncOutlookNow).toHaveBeenCalledWith({ scope: "all", date: undefined }),
    );
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

  // `busy` means a sync is ALREADY RUNNING - the one that fires on launch, or the tray's. That is not a
  // failure, and reporting it as one is what put a red "Could not sync the calendar" on screen for the whole
  // of every launch sync. The run in progress refreshes the same calendar, so join it: wait for the shell to
  // land and then refetch, exactly as if we had started it.
  it("joins a sync already in progress rather than calling it a failure", async () => {
    const shell = fakeShell({ started: false, reason: "busy" });
    const d = deps({ outlook: true, syncOutlookNow: shell.syncOutlookNow, onOutlookState: shell.onOutlookState });

    const run = runCalendarSync("all", d);
    await vi.waitFor(() => expect(shell.syncOutlookNow).toHaveBeenCalled());
    expect(d.refetchEvents).not.toHaveBeenCalled(); // still waiting on the run in flight

    shell.emit("idle");
    const result = await run;

    expect(result.outlookReason).toBeUndefined();
    expect(d.refetchEvents).toHaveBeenCalledTimes(1);
  });

  // The subtle half of the same bug. Joining means attaching to a run that is already under way, so the
  // waiter cannot require a non-idle phase before it will accept `idle` as "finished" - by then the only
  // event left to see IS the idle one. Without this it would wait out the full timeout instead.
  it("does not hang when the run it joined is already on its last phase", async () => {
    vi.useFakeTimers();
    try {
      const shell = fakeShell({ started: false, reason: "busy" });
      const d = deps({
        outlook: true,
        syncOutlookNow: shell.syncOutlookNow,
        onOutlookState: shell.onOutlookState,
        timeoutMs: 150_000,
      });

      const run = runCalendarSync("all", d);
      await vi.waitFor(() => expect(shell.syncOutlookNow).toHaveBeenCalled());
      shell.emit("idle"); // the only event it will ever see
      await vi.advanceTimersByTimeAsync(10);
      const result = await run;

      expect(result.outlookReason).toBeUndefined();
      expect(d.refetchEvents).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("syncErrorKey", () => {
  // A refusal is not automatically a failure worth shouting about. A cooldown means a sync ran moments ago,
  // so the calendar is already fresh; `busy` is handled by joining the run and never reaches here.
  it("stays quiet when there is nothing wrong", () => {
    expect(syncErrorKey(undefined)).toBeNull();
    expect(syncErrorKey("cooldown")).toBeNull();
  });

  // The original message said only "Could not sync the calendar", which told the user nothing and made the
  // first real failure genuinely hard to diagnose - the reason was thrown away at exactly the point it
  // mattered. Every reason the shell can give now maps to something actionable.
  it("names what actually went wrong", () => {
    for (const reason of ["unavailable", "not-installed", "new-outlook", "not-windows"]) {
      expect(syncErrorKey(reason), reason).toBe("calSyncFailedUnavailable");
    }
    expect(syncErrorKey("timeout")).toBe("calSyncFailedTimeout");
    expect(syncErrorKey("denied")).toBe("calSyncFailedDenied");
    expect(syncErrorKey("disabled")).toBe("calSyncFailedDisabled");
  });

  it("falls back to the generic message for a reason it does not know", () => {
    expect(syncErrorKey("something-new")).toBe("calSyncFailed");
    expect(syncErrorKey("error")).toBe("calSyncFailed");
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
