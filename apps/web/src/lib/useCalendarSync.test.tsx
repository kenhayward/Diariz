import { render, screen, act, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { useState } from "react";
import { StatusProvider, useStatus } from "./status";

vi.mock("./api", () => ({
  api: { getUserSettings: vi.fn().mockResolvedValue({ outlookSyncEnabled: true }) },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { useCalendarSync } from "./calendarSync";

/// A desktop shell that starts a sync and then goes quiet - the shape every stuck message has in common.
/// Nothing here replays on subscribe, matching the real bridge, so only `emit` can end a run.
function installShell(over: Record<string, unknown> = {}) {
  const listeners: ((s: { phase: string }) => void)[] = [];
  const shell = {
    canSyncOutlook: true,
    outlookAvailable: async () => true,
    syncOutlookNow: vi.fn().mockResolvedValue({ started: true }),
    onOutlookState: (cb: (s: { phase: string }) => void) => {
      listeners.push(cb);
      return () => listeners.splice(listeners.indexOf(cb), 1);
    },
    reportOutlookReady: () => {},
    onOutlookPush: () => () => {},
    reportOutlookResult: () => {},
    ...over,
  };
  (window as unknown as { diariz: unknown }).diariz = shell;
  return { emit: (phase: string) => listeners.forEach((cb) => cb({ phase })) };
}

/// The toolbar: mounts the hook, and can be unmounted independently of the status provider above it - which
/// is exactly the real arrangement (StatusProvider lives in the app shell, ListToolbar inside the panel).
function Toolbar() {
  const { sync, busy } = useCalendarSync();
  // `busy` is what disables the real buttons, so a stuck one is a calendar you can never sync again.
  return (
    <>
      <button onClick={() => sync("all")}>start-sync</button>
      <span data-testid="busy">{String(busy)}</span>
    </>
  );
}

/// Reports the status from OUTSIDE the toolbar, so it still sees the bar after the toolbar is gone.
function Probe() {
  const { status } = useStatus();
  return <span data-testid="msg">{status ? status.text : "none"}</span>;
}

function renderHarness() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  function App() {
    const [mounted, setMounted] = useState(true);
    return (
      <StatusProvider>
        <Probe />
        <button onClick={() => setMounted(false)}>unmount-toolbar</button>
        {mounted && <Toolbar />}
      </StatusProvider>
    );
  }
  render(
    <QueryClientProvider client={qc}>
      <App />
    </QueryClientProvider>,
  );
}

const msg = () => screen.getByTestId("msg").textContent;
const busy = () => screen.getByTestId("busy").textContent;

/// Let the mount effects settle - the shell-availability probe is a promise, and until it resolves the hook
/// treats this as a browser and never talks to the shell at all.
async function settle() {
  await act(async () => {
    await Promise.resolve();
  });
}

describe("useCalendarSync status message", () => {
  beforeEach(() => {
    installShell();
  });
  afterEach(() => {
    delete (window as unknown as { diariz?: unknown }).diariz;
    vi.useRealTimers();
  });

  /// The bug: the progress message is pushed `sticky`, so nothing ever expires it, and the effect's cleanup
  /// only cleared its interval. Unmounting the toolbar mid-sync therefore froze the message on screen for the
  /// rest of the session - the counter stopped, and the remounted toolbar refused to clear a message its own
  /// `pushed` ref said it had not written.
  it("clears its progress message when the toolbar unmounts mid-sync", async () => {
    renderHarness();

    // Wait for the shell probe to settle, so the sync takes the desktop path and hangs on the shell.
    await waitFor(() => expect(screen.getByText("start-sync")).toBeTruthy());
    await act(async () => {
      screen.getByText("start-sync").click();
    });
    expect(msg()).toContain("Syncing calendar");

    await act(async () => {
      screen.getByText("unmount-toolbar").click();
    });

    expect(msg()).toBe("none");
  });

  /// The other half of the same guard: a message this hook did NOT write must survive its unmount, or the
  /// toolbar would wipe an upload or recording message on its way out.
  it("leaves a message it did not write alone", async () => {
    renderHarness();
    await waitFor(() => expect(screen.getByText("start-sync")).toBeTruthy());

    // Nothing pushed by the hook - the bar is showing somebody else's message.
    await act(async () => {
      screen.getByText("unmount-toolbar").click();
    });

    expect(msg()).toBe("none"); // still nothing, and crucially no crash
  });
});

/// The shell can stop answering. Its reader is hard-capped at 120s (`READ_TIMEOUT_MS` in outlookHost.js), so
/// silence past that is not a slow mailbox - it is a shell that will never come back. Two ways in, and until
/// now the second had no bound at all.
describe("useCalendarSync when the shell goes quiet", () => {
  beforeEach(() => vi.useFakeTimers());

  /// Joining a run the shell reports as already in flight, which then never reaches `idle`. The wait itself is
  /// a correct backstop; what matters is that it ENDS, and that the reason survives on screen. The shell-phase
  /// half of `busy` outlives our run here, and a progress tick painting over the verdict would leave the user
  /// with a message that says nothing and then vanishes.
  it("ends a joined run that never finishes, and leaves the reason on screen", async () => {
    const shell = installShell({
      syncOutlookNow: vi.fn().mockResolvedValue({ started: false, reason: "busy" }),
    });
    renderHarness();
    await settle();

    await act(async () => {
      screen.getByText("start-sync").click();
    });
    // In act, or the phase lands after the clock below has already run past the stale window and the timer
    // that bounds it would be armed too late to fire.
    act(() => shell.emit("reading")); // the shell says it is working...
    expect(msg()).toContain("Syncing calendar");

    // ...and then says nothing, ever again.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(200_000);
    });

    expect(msg()).toContain("took too long");
    expect(msg()).not.toContain("Syncing calendar");
    expect(busy()).toBe("false"); // and the buttons work again
  });

  /// No click at all: the shell announces work on its own (the launch sync, or the tray's) and then wedges.
  /// The shipped shell can do exactly this - it parks in `pushing` until the renderer reports its POST, and
  /// nothing resets it if that report never comes. `busy` had no bound here, so the buttons stayed dead and
  /// the message stayed up for the rest of the session.
  it("stops believing a shell phase that never returns to idle", async () => {
    const shell = installShell();
    renderHarness();
    await settle();

    act(() => shell.emit("pushing"));
    expect(msg()).toContain("Syncing calendar");
    expect(busy()).toBe("true");

    await act(async () => {
      await vi.advanceTimersByTimeAsync(200_000);
    });

    expect(msg()).toBe("none");
    expect(busy()).toBe("false");
  });

  /// The bound must not cut a healthy sync short: every phase change restarts the clock, so a run that keeps
  /// reporting is left alone however long it takes.
  it("keeps waiting while the shell is still reporting progress", async () => {
    const shell = installShell();
    renderHarness();
    await settle();

    act(() => shell.emit("reading"));
    for (let i = 0; i < 3; i++) {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(100_000);
      });
      act(() => shell.emit(i % 2 === 0 ? "pushing" : "reading"));
    }

    // 300s in, still working, still believed.
    expect(busy()).toBe("true");
    expect(msg()).toContain("Syncing calendar");

    act(() => shell.emit("idle"));
    expect(busy()).toBe("false");
    expect(msg()).toBe("none");
  });
});
