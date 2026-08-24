import { describe, it, expect, vi, afterEach } from "vitest";
import {
  canSyncOutlook, chunkEvents, onOutlookPush, onOutlookState, outlookAvailable,
  reportOutlookReady, reportOutlookResult, syncOutlookNow,
} from "./outlookSync";
import type { OutlookShellState } from "./outlookSync";

/// The bridge has to be inert in a plain browser: the module is imported by a component that mounts
/// unconditionally, so anything here reaching for a missing `window.diariz` would break the web app for
/// everyone who is not on the Windows desktop.
describe("outlookSync in a plain browser", () => {
  afterEach(() => {
    delete (window as { diariz?: unknown }).diariz;
  });

  it("reports that it cannot sync", () => {
    expect(canSyncOutlook()).toBe(false);
  });

  it("reports Outlook unavailable rather than throwing", async () => {
    await expect(outlookAvailable()).resolves.toBe(false);
  });

  it("makes the fire-and-forget calls no-ops", () => {
    expect(() => reportOutlookReady({
      enabled: true, pastDays: 30, futureDays: 180, skipPrivate: true, includeBody: true,
    })).not.toThrow();
    expect(() => reportOutlookResult({ ok: true })).not.toThrow();
  });

  it("refuses a sync with a reason instead of failing", async () => {
    await expect(syncOutlookNow()).resolves.toEqual({ started: false, reason: "unavailable" });
  });

  it("returns working unsubscribers from the subscriptions", () => {
    const offPush = onOutlookPush(() => {});
    const offState = onOutlookState(() => {});
    expect(() => { offPush(); offState(); }).not.toThrow();
  });
});

describe("outlookSync with a desktop shell", () => {
  afterEach(() => {
    delete (window as { diariz?: unknown }).diariz;
  });

  it("delegates to the shell when it is present", async () => {
    const unsubscribe = vi.fn();
    const shell = {
      canSyncOutlook: true,
      outlookAvailable: vi.fn().mockResolvedValue(true),
      syncOutlookNow: vi.fn().mockResolvedValue({ started: true }),
      reportOutlookReady: vi.fn(),
      onOutlookPush: vi.fn().mockReturnValue(unsubscribe),
    };
    (window as { diariz?: unknown }).diariz = shell;

    expect(canSyncOutlook()).toBe(true);
    await expect(outlookAvailable()).resolves.toBe(true);
    await expect(syncOutlookNow()).resolves.toEqual({ started: true });
    expect(onOutlookPush(() => {})).toBe(unsubscribe);
  });

  /// A shell that throws while answering is, for our purposes, unavailable - the UI should hide the button,
  /// not surface an exception from a capability probe.
  it("treats a shell that errors as unavailable", async () => {
    (window as { diariz?: unknown }).diariz = {
      canSyncOutlook: true,
      outlookAvailable: vi.fn().mockRejectedValue(new Error("COM blew up")),
    };
    await expect(outlookAvailable()).resolves.toBe(false);
  });
});

describe("chunkEvents", () => {
  it("splits into pages of the requested size", () => {
    expect(chunkEvents([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
  });

  it("keeps a single short page whole", () => {
    expect(chunkEvents([1, 2], 250)).toEqual([[1, 2]]);
  });

  /// An empty window still needs one page. A user who cleared their calendar, or whose meetings all moved out
  /// of the window, must still get a final page - otherwise the server never sweeps and the old copies stay
  /// mirrored forever.
  it("yields one empty page for an empty window, so the sweep still runs", () => {
    expect(chunkEvents([], 250)).toEqual([[]]);
  });

  it("rejects a non-positive size rather than looping forever", () => {
    expect(() => chunkEvents([1], 0)).toThrow();
  });
});

/// The shell PUSHES phase changes and, until now, replayed nothing - so a subscriber that arrived while a sync
/// was already reading never learned one was running. That is what left the Calendar toolbar's sync buttons
/// live (and the status bar silent) through the whole of every launch sync: the toolbar's subscription is
/// established per mount, and any mount after the run started was blind to it.
describe("onOutlookState replaying the current phase", () => {
  afterEach(() => {
    delete (window as { diariz?: unknown }).diariz;
  });

  /// A shell whose push channel and current-state accessor the test drives independently.
  function fakeShell(current?: Promise<OutlookShellState>) {
    let push: ((state: OutlookShellState) => void) | undefined;
    const off = vi.fn();
    (window as { diariz?: unknown }).diariz = {
      onOutlookState: (cb: (state: OutlookShellState) => void) => {
        push = cb;
        return off;
      },
      outlookState: current ? () => current : undefined,
    };
    return { emit: (state: OutlookShellState) => push?.(state), off };
  }

  /// Drain the microtask queue, so anything the replay chain still owes has been delivered by the time the
  /// assertion runs. The chain is `then` -> `catch`, so one turn is not enough.
  const flush = () => new Promise<void>((r) => setTimeout(r, 0));

  it("tells a new subscriber about a sync that is already running", async () => {
    fakeShell(Promise.resolve({ phase: "reading" }));
    const seen: OutlookShellState[] = [];

    onOutlookState((s) => seen.push(s));
    await vi.waitFor(() => expect(seen).toEqual([{ phase: "reading" }]));
  });

  /// The replay resolves a tick later than the subscribe, so a phase change can overtake it. Delivering the
  /// stale answer afterwards would tell the app a finished sync was still running - and `busy` would stay on
  /// until the 150s staleness timer gave up on it.
  it("drops the replay when a real push has already arrived", async () => {
    let resolve!: (state: OutlookShellState) => void;
    const shell = fakeShell(new Promise<OutlookShellState>((r) => { resolve = r; }));
    const seen: OutlookShellState[] = [];

    onOutlookState((s) => seen.push(s));
    shell.emit({ phase: "idle" });
    resolve({ phase: "reading" }); // the shell's answer from before the push, arriving late

    // Drained rather than polled with `waitFor`: waitFor checks once immediately, and `seen` is already
    // correct at that instant - so it would pass before the stale replay it exists to catch could land.
    await flush();
    expect(seen).toEqual([{ phase: "idle" }]);
  });

  it("delivers nothing to a subscriber that has already unsubscribed", async () => {
    let resolve!: (state: OutlookShellState) => void;
    fakeShell(new Promise<OutlookShellState>((r) => { resolve = r; }));
    const seen: OutlookShellState[] = [];

    onOutlookState((s) => seen.push(s))();
    resolve({ phase: "reading" });

    await flush();
    expect(seen).toEqual([]);
  });

  /// Web and desktop ship separately, so a current web build routinely runs against a shell with no accessor.
  /// It must simply not replay, rather than throwing on the way past.
  it("subscribes without replaying on a shell that cannot answer", async () => {
    const shell = fakeShell(undefined);
    const seen: OutlookShellState[] = [];

    expect(() => onOutlookState((s) => seen.push(s))).not.toThrow();
    await flush();
    expect(seen).toEqual([]);

    shell.emit({ phase: "pushing" });
    expect(seen).toEqual([{ phase: "pushing" }]);
  });
});
