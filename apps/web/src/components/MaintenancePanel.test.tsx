import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

const logout = vi.fn();
vi.mock("../auth", () => ({ useAuth: () => ({ logout }) }));
vi.mock("../lib/api", () => ({
  api: {
    backupUrl: () => "/api/maintenance/backup?access_token=t",
    restoreBackup: vi.fn(),
    runTagBackfill: vi.fn().mockResolvedValue({ enqueued: 0 }),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import MaintenancePanel from "./MaintenancePanel";

function chooseFileAndConfirm() {
  const file = new File(["zip"], "backup.zip", { type: "application/zip" });
  fireEvent.change(screen.getByLabelText(/choose a backup file/i), { target: { files: [file] } });
  fireEvent.click(screen.getByRole("checkbox", { name: /permanently replace all current data/i }));
  fireEvent.click(screen.getByRole("button", { name: /^restore$/i }));
}

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
