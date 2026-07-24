import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from "vitest";

const logout = vi.fn();
vi.mock("../auth", () => ({ useAuth: () => ({ logout }) }));
vi.mock("../lib/api", () => ({
  api: {
    backupUrl: () => "/api/maintenance/backup?access_token=t",
    backupStatus: vi.fn(),
    restoreBackup: vi.fn(),
    runTagBackfill: vi.fn().mockResolvedValue({ enqueued: 0 }),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import MaintenancePanel from "./MaintenancePanel";

const idle = { running: false, phase: null, objectsArchived: 0, startedAt: null };

function chooseFileAndConfirm() {
  const file = new File(["zip"], "backup.zip", { type: "application/zip" });
  fireEvent.change(screen.getByLabelText(/choose a backup file/i), { target: { files: [file] } });
  fireEvent.click(screen.getByRole("checkbox", { name: /permanently replace all current data/i }));
  fireEvent.click(screen.getByRole("button", { name: /^restore$/i }));
}

// The download is a plain anchor; jsdom logs "Not implemented: navigation" if the click is left to default.
const blockNavigation = (e: Event) => e.preventDefault();

describe("MaintenancePanel backup", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.addEventListener("click", blockNavigation, true);
  });
  afterEach(() => {
    document.removeEventListener("click", blockNavigation, true);
    vi.useRealTimers();
  });

  it("reports the backup as running while the server assembles the archive, then clears", async () => {
    vi.useFakeTimers();
    (api.backupStatus as Mock)
      .mockResolvedValueOnce({
        running: true, phase: "Objects", objectsArchived: 12, startedAt: "2026-07-24T10:00:00Z",
      })
      .mockResolvedValue(idle);
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });

    // The build is under way: the panel names the stage and how many files are in so far.
    expect(screen.getByText(/archiving files \(12 so far\)/i)).toBeTruthy();

    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });

    // The server reports idle again, so the archive is built and the browser download has taken over.
    expect(screen.queryByText(/archiving files/i)).toBeNull();
    expect(screen.getByText(/backup ready/i)).toBeTruthy();
  });

  it("stops watching when the request never reaches the server", async () => {
    vi.useFakeTimers();
    (api.backupStatus as Mock).mockResolvedValue(idle); // never reports a build - e.g. the click was blocked
    render(<MaintenancePanel />);

    fireEvent.click(screen.getByRole("link", { name: /download backup/i }));
    await act(async () => { await vi.advanceTimersByTimeAsync(1600); });
    expect(screen.getByText(/preparing your backup/i)).toBeTruthy();

    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });

    // Gave up rather than spinning forever, and made no claim that a backup was produced.
    expect(screen.queryByText(/preparing your backup/i)).toBeNull();
    expect(screen.queryByText(/backup ready/i)).toBeNull();
  });
});

describe("MaintenancePanel restore", () => {
  beforeEach(() => vi.clearAllMocks());

  it("reports a forward-migrated restore + the restart hint, and does not sign out", async () => {
    (api.restoreBackup as Mock).mockResolvedValue({
      restored: true, migratedFrom: "m1", migratedTo: "m3", restartRecommended: true,
    });
    render(<MaintenancePanel />);
    chooseFileAndConfirm();

    expect(await screen.findByText(/upgraded its data/i)).toBeTruthy();
    expect(screen.getByText(/restart the app/i)).toBeTruthy();
    // A forward-migrated instance is already consistent with the running code - no forced sign-out.
    expect(logout).not.toHaveBeenCalled();
  });

  it("says the server is applying the backup once the upload has finished", async () => {
    // Upload progress hits 100% long before the restore is done - the destructive server-side work (load the
    // dump, migrate, replace the object store) is the slow part, and a stuck "100%" reads as a hang.
    (api.restoreBackup as Mock).mockImplementation(
      (_file: File, onProgress?: (p: number) => void) => {
        onProgress?.(100);
        return new Promise(() => {}); // still working
      },
    );
    render(<MaintenancePanel />);
    chooseFileAndConfirm();

    expect(await screen.findByText(/applying the backup/i)).toBeTruthy();
    expect(screen.queryByText(/restoring/i)).toBeNull();
  });

  it("shows the plain success line for a same-version restore (no migration/restart hint)", async () => {
    (api.restoreBackup as Mock).mockResolvedValue({
      restored: true, migratedFrom: "m3", migratedTo: "m3", restartRecommended: false,
    });
    render(<MaintenancePanel />);
    chooseFileAndConfirm();

    await waitFor(() => expect(api.restoreBackup).toHaveBeenCalled());
    expect(screen.queryByText(/upgraded its data/i)).toBeNull();
    expect(screen.queryByText(/restart the app/i)).toBeNull();
  });
});
