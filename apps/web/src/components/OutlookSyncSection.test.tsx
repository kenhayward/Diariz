import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import OutlookSyncSection from "./OutlookSyncSection";
import { api } from "../lib/api";
import type { OutlookSource } from "../lib/types";

vi.mock("../lib/api", async () => {
  const actual = await vi.importActual<typeof import("../lib/api")>("../lib/api");
  return {
    ...actual,
    api: {
      getUserSettings: vi.fn(),
      listOutlookSources: vi.fn(),
      updateUserSettings: vi.fn().mockResolvedValue(undefined),
      updateOutlookSource: vi.fn().mockResolvedValue({}),
      deleteOutlookSource: vi.fn().mockResolvedValue(undefined),
    },
  };
});

const device: OutlookSource = {
  id: "src-1",
  deviceId: "dev-1",
  deviceName: "WORK-PC",
  mailboxName: "ken@example.test",
  displayName: "Outlook (WORK-PC)",
  color: "#0F6CBD",
  enabled: true,
  pastDays: 30,
  futureDays: 180,
  skipPrivate: true,
  includeBody: true,
  lastSyncedAt: "2026-08-07T09:00:00Z",
  lastError: null,
  eventCount: 142,
};

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <OutlookSyncSection />
    </QueryClientProvider>,
  );
}

describe("OutlookSyncSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: false });
    (api.listOutlookSources as Mock).mockResolvedValue([]);
  });

  afterEach(() => {
    delete (window as { diariz?: unknown }).diariz;
    vi.restoreAllMocks();
  });

  /// A browser user must still be able to read what this does, see their connected machines, and revoke -
  /// so the section is fully present, and only the button that could not work is absent.
  it("explains where syncing happens instead of offering a dead button in a browser", async () => {
    renderSection();

    expect(await screen.findByText(/desktop app on Windows/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /sync now/i })).toBeNull();
  });

  it("turns the opt-in on without a confirmation", async () => {
    renderSection();
    const box = await screen.findByRole("checkbox", { name: /sync my desktop outlook calendar/i });

    fireEvent.click(box);

    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalledWith({ outlookSyncEnabled: true }));
  });

  /// Turning it off erases every mirrored meeting, so it must not be a single stray click.
  it("confirms before turning the opt-in off, and does nothing if declined", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    renderSection();

    // Wait for the setting to land, not just for the box to exist - clicking before it reflects "on" would
    // toggle it on rather than off, and never reach the confirm.
    const box = await screen.findByRole<HTMLInputElement>("checkbox", { name: /sync my desktop outlook calendar/i });
    await waitFor(() => expect(box.checked).toBe(true));
    fireEvent.click(box);

    expect(confirm).toHaveBeenCalled();
    expect(api.updateUserSettings).not.toHaveBeenCalled();
  });

  it("shows a connected machine with its mailbox, event count and last sync", async () => {
    (api.listOutlookSources as Mock).mockResolvedValue([device]);
    renderSection();

    expect(await screen.findByText(/ken@example.test/)).toBeTruthy();
    expect(screen.getByText(/142 meetings/)).toBeTruthy();
  });

  /// A connector broken on one PC has to be visible from any other device, or from a browser - which is the
  /// whole reason the failure is stored server-side rather than kept in the shell.
  it("surfaces a device's last sync failure", async () => {
    (api.listOutlookSources as Mock).mockResolvedValue([
      { ...device, lastError: "Diariz needs classic Outlook for Windows." },
    ]);
    renderSection();

    expect(await screen.findByText(/Last sync failed: Diariz needs classic Outlook/)).toBeTruthy();
  });

  it("confirms before disconnecting a machine", async () => {
    (api.listOutlookSources as Mock).mockResolvedValue([device]);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderSection();

    fireEvent.click(await screen.findByRole("button", { name: /disconnect/i }));

    expect(confirm).toHaveBeenCalled();
    await waitFor(() => expect(api.deleteOutlookSource).toHaveBeenCalledWith("src-1"));
  });

  it("hides a machine without disconnecting it", async () => {
    (api.listOutlookSources as Mock).mockResolvedValue([device]);
    renderSection();

    fireEvent.click(await screen.findByRole("checkbox", { name: /^shown$/i }));

    await waitFor(() => expect(api.updateOutlookSource).toHaveBeenCalledWith("src-1", { enabled: false }));
    expect(api.deleteOutlookSource).not.toHaveBeenCalled();
  });

  it("offers Sync now on a desktop shell that can reach Outlook and is opted in", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    (api.listOutlookSources as Mock).mockResolvedValue([device]);
    (window as { diariz?: unknown }).diariz = {
      canSyncOutlook: true,
      outlookAvailable: vi.fn().mockResolvedValue(true),
      syncOutlookNow: vi.fn().mockResolvedValue({ started: true }),
      onOutlookState: vi.fn().mockReturnValue(() => {}),
    };
    renderSection();

    const button = await screen.findByRole("button", { name: /sync now/i });
    fireEvent.click(button);

    await waitFor(() =>
      expect((window as { diariz: { syncOutlookNow: Mock } }).diariz.syncOutlookNow).toHaveBeenCalled(),
    );
  });

  /// The new Outlook exposes no COM at all, so this is the case a growing share of Windows users will hit -
  /// it needs its own explanation rather than a button that silently fails.
  it("explains rather than offering the button when the shell cannot reach Outlook", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    (window as { diariz?: unknown }).diariz = {
      canSyncOutlook: true,
      outlookAvailable: vi.fn().mockResolvedValue(false),
      onOutlookState: vi.fn().mockReturnValue(() => {}),
    };
    renderSection();

    expect(await screen.findByText(/needs classic Outlook for Windows/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /sync now/i })).toBeNull();
  });
});
