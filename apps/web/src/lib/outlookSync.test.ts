import { describe, it, expect, vi, afterEach } from "vitest";
import {
  canSyncOutlook, chunkEvents, onOutlookPush, onOutlookState, outlookAvailable,
  reportOutlookReady, reportOutlookResult, syncOutlookNow,
} from "./outlookSync";

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
