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
  const { sync } = useCalendarSync();
  return <button onClick={() => sync("all")}>start-sync</button>;
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
