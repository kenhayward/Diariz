import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import type { ReactNode } from "react";
import { useHubPopover } from "./hub/hubPopovers";

// WorkspaceLayout pulls in a stack of providers/side-effect components that have nothing to do with the
// popover wiring under test; stub them so this stays a focused wiring test, matching the pattern other
// shell tests use (TopBar.test.tsx stubs Recorder, Workspace.test.tsx stubs the panels).
vi.mock("./TourOverlay", () => ({ default: () => null }));
vi.mock("./StatusBar", () => ({ default: () => null }));
vi.mock("./ThemeSync", () => ({ default: () => null }));
vi.mock("./OutlookSyncBridge", () => ({ default: () => null }));
vi.mock("../lib/uploadContext", () => ({
  UploadProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
}));
vi.mock("../lib/tour", () => ({
  TourProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
}));
vi.mock("../lib/status", () => ({
  StatusProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
}));
vi.mock("../lib/rooms", () => ({
  RoomProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
}));
vi.mock("../lib/toast", () => ({
  ToastProvider: ({ children }: { children: ReactNode }) => <>{children}</>,
}));

// TopBar and Workspace stand in for the real components, each a probe calling useHubPopover() itself - the
// same way the real Recorder (inside TopBar) and UserMenu (inside Workspace) do. A stub that just renders
// static markup can't catch what this test exists to catch: whether the *provider WorkspaceLayout renders*
// actually reaches both subtrees, or whether one of them silently falls back to its own local state because
// it sits outside the provider (see hub/hubPopovers.tsx's no-provider fallback).
vi.mock("./TopBar", () => ({
  default: function TopBarProbe() {
    const { toggle, isOpen } = useHubPopover();
    return (
      <div>
        <button type="button" onClick={() => toggle("source")}>
          topbar-toggle-source
        </button>
        <span data-testid="topbar-sees-acct">{String(isOpen("acct"))}</span>
      </div>
    );
  },
}));
vi.mock("./Workspace", () => ({
  default: function WorkspaceProbe() {
    const { toggle, isOpen } = useHubPopover();
    return (
      <div>
        <button type="button" onClick={() => toggle("acct")}>
          workspace-toggle-acct
        </button>
        <span data-testid="workspace-sees-source">{String(isOpen("source"))}</span>
      </div>
    );
  },
}));

import WorkspaceLayout from "./WorkspaceLayout";

describe("WorkspaceLayout popover wiring", () => {
  // The one requirement the hoist out of TopBar exists to preserve: the recorder cluster (in TopBar today,
  // moving into a capture bar inside Workspace in a later step) and the account menu (in Workspace's room
  // row) must share ONE popover-open state, even though they live in different subtrees of WorkspaceLayout.
  it("shares one HubPopoverProvider between TopBar and Workspace", () => {
    render(<WorkspaceLayout />);

    // Toggling "source" from the TopBar probe must be visible to the Workspace probe.
    fireEvent.click(screen.getByRole("button", { name: "topbar-toggle-source" }));
    expect(screen.getByTestId("workspace-sees-source").textContent).toBe("true");

    // Toggling "acct" from the Workspace probe closes "source" and is visible back on the TopBar probe.
    fireEvent.click(screen.getByRole("button", { name: "workspace-toggle-acct" }));
    expect(screen.getByTestId("topbar-sees-acct").textContent).toBe("true");
    expect(screen.getByTestId("workspace-sees-source").textContent).toBe("false");
  });
});
