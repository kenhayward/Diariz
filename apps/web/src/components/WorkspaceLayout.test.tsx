import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import type { ReactNode } from "react";
import { useHubPopover } from "./hub/hubPopovers";

// WorkspaceLayout pulls in a stack of providers/side-effect components that have nothing to do with the
// popover wiring under test; stub them so this stays a focused wiring test, matching the pattern other
// shell tests use (CaptureBar.test.tsx stubs Recorder, Workspace.test.tsx stubs the panels).
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

// Both popover consumers now live inside Workspace itself: the capture bar's recorder cluster and the
// room row's account menu. Stand in for Workspace with a probe that calls useHubPopover() twice - once per
// consumer role - the same way the real CaptureBar (recorder, via Recorder) and UserMenu (account row) each
// call it independently deep in Workspace's real tree. A stub that renders static markup can't catch what
// this test exists to catch: whether the *provider WorkspaceLayout renders* actually reaches this subtree,
// or whether a consumer silently falls back to its own local state because Workspace (or part of it) sits
// outside the provider (see hub/hubPopovers.tsx's no-provider fallback - it is per call-site, not shared).
vi.mock("./Workspace", () => ({
  default: function WorkspaceProbe() {
    const capture = useHubPopover();
    const account = useHubPopover();
    return (
      <div>
        <button type="button" onClick={() => capture.toggle("source")}>
          capture-toggle-source
        </button>
        <span data-testid="account-sees-source">{String(account.isOpen("source"))}</span>
        <button type="button" onClick={() => account.toggle("acct")}>
          account-toggle-acct
        </button>
        <span data-testid="capture-sees-acct">{String(capture.isOpen("acct"))}</span>
      </div>
    );
  },
}));

import WorkspaceLayout from "./WorkspaceLayout";

describe("WorkspaceLayout popover wiring", () => {
  // The one requirement HubPopoverProvider exists to preserve, post-header-removal: the capture bar's
  // recorder cluster and the account menu - both now inside Workspace - must share ONE popover-open state,
  // fed by the provider WorkspaceLayout wraps around Workspace.
  it("shares one HubPopoverProvider across the workspace's popover consumers", () => {
    render(<WorkspaceLayout />);

    // Toggling "source" from the capture-role probe must be visible to the account-role probe.
    fireEvent.click(screen.getByRole("button", { name: "capture-toggle-source" }));
    expect(screen.getByTestId("account-sees-source").textContent).toBe("true");

    // Toggling "acct" from the account-role probe closes "source" and is visible back on the capture probe.
    fireEvent.click(screen.getByRole("button", { name: "account-toggle-acct" }));
    expect(screen.getByTestId("capture-sees-acct").textContent).toBe("true");
    expect(screen.getByTestId("account-sees-source").textContent).toBe("false");
  });
});
