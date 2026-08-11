import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { SelectionProvider } from "../../lib/selection";
import { StatusProvider } from "../../lib/status";
import StatusBar from "../StatusBar";
import type { RecordingSummary } from "../../lib/types";

// Only what this leaf reaches for, not the panel's whole mock wall.
vi.mock("../../lib/api", () => ({
  api: {
    getUserSettings: vi.fn().mockResolvedValue({ outlookSyncEnabled: false }),
    getCalendarEvents: vi.fn().mockResolvedValue([]),
    listRecordings: vi.fn().mockResolvedValue([]),
    getUserStorage: vi.fn().mockResolvedValue(undefined),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import ListToolbar from "./ListToolbar";

const recordings: RecordingSummary[] = [];

/// The toolbar as the panel mounts it, with the status bar underneath so what a sync tells the user is
/// observable rather than inferred from internals.
function renderToolbar(over: Partial<Parameters<typeof ListToolbar>[0]> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={qc}>
      <StatusProvider>
        <SelectionProvider>
          <ListToolbar
            recordings={recordings}
            listMode
            calendarMode={false}
            isPersonalRoom
            allowFolders
            sections={[]}
            drillSectionId={null}
            onError={() => {}}
            {...over}
          />
          <StatusBar />
        </SelectionProvider>
      </StatusProvider>
    </QueryClientProvider>,
  );
  return { ...view, qc };
}

/// A desktop shell whose sync phase the test drives. The real one pushes state changes and replays nothing on
/// subscribe, so `emit` is the only way a run learns the shell has finished.
function fakeShell(started: { started: boolean; reason?: string } = { started: true }) {
  const listeners: ((s: { phase: string }) => void)[] = [];
  const syncOutlookNow = vi.fn().mockResolvedValue(started);
  (window as { diariz?: unknown }).diariz = {
    canSyncOutlook: true,
    outlookAvailable: vi.fn().mockResolvedValue(true),
    syncOutlookNow,
    onOutlookState: (cb: (s: { phase: string }) => void) => {
      listeners.push(cb);
      return () => listeners.splice(listeners.indexOf(cb), 1);
    },
  };
  return { syncOutlookNow, emit: (phase: string) => act(() => listeners.forEach((cb) => cb({ phase }))) };
}

describe("ListToolbar calendar sync", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: false });
    delete (window as { diariz?: unknown }).diariz;
  });

  // The buttons belong to the Calendar, so they only appear with it - but they live up here rather than in the
  // day view, where they used to sit below the month grid and looked like part of the calendar's chrome.
  it("offers both syncs on the Calendar tab", async () => {
    renderToolbar({ calendarMode: true, listMode: false });

    expect(await screen.findByRole("button", { name: /sync today/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /sync calendar/i })).toBeTruthy();
  });

  it("offers neither on the list", () => {
    renderToolbar();

    expect(screen.queryByRole("button", { name: /sync today/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /sync calendar/i })).toBeNull();
  });

  // A shared room shows only its own recordings on the calendar - there is no event overlay to sync.
  it("offers neither in a shared room", () => {
    renderToolbar({ calendarMode: true, listMode: false, isPersonalRoom: false });

    expect(screen.queryByRole("button", { name: /sync calendar/i })).toBeNull();
  });

  // One sync covers every source. Google and the .ics feeds are read live by the server, so refetching the
  // overlay is their refresh; the browser has nothing else to do and must not be left with a dead button.
  it("refreshes the calendar in a plain browser", async () => {
    const { qc } = renderToolbar({ calendarMode: true, listMode: false });
    const invalidate = vi.spyOn(qc, "invalidateQueries");

    fireEvent.click(await screen.findByRole("button", { name: /sync calendar/i }));

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith(expect.objectContaining({ queryKey: ["calendar-events"] })),
    );
  });

  // Opting in is the gate on reading a local Outlook calendar at all. Without it the button still works - it
  // just refreshes what the server already has - but nothing may touch the mailbox.
  it("does not ask the shell when the user has not opted in to Outlook", async () => {
    const { syncOutlookNow } = fakeShell();
    const { qc } = renderToolbar({ calendarMode: true, listMode: false });
    const invalidate = vi.spyOn(qc, "invalidateQueries");

    fireEvent.click(await screen.findByRole("button", { name: /sync calendar/i }));

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith(expect.objectContaining({ queryKey: ["calendar-events"] })),
    );
    expect(syncOutlookNow).not.toHaveBeenCalled();
  });

  it("asks the desktop shell for today only when the quick sync is pressed", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { syncOutlookNow, emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false });

    fireEvent.click(await screen.findByRole("button", { name: /sync today/i }));

    await waitFor(() => expect(syncOutlookNow).toHaveBeenCalledWith({ scope: "today" }));
    emit("reading");
    emit("idle");
  });

  // The whole point of the status line: a 30-second sync with no feedback is indistinguishable from a button
  // that did nothing. It has to appear on the click and go away when the run is over.
  it("counts up in the status bar while syncing, and clears when it finishes", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false });

    fireEvent.click(await screen.findByRole("button", { name: /sync calendar/i }));

    expect(await screen.findByText(/syncing calendar 0s/i)).toBeTruthy();
    emit("reading");
    emit("idle");
    await waitFor(() => expect(screen.queryByText(/syncing calendar/i)).toBeNull());
  });

  it("names the quick sync in the status bar, so the two are told apart", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false });

    fireEvent.click(await screen.findByRole("button", { name: /sync today/i }));

    expect(await screen.findByText(/syncing calendar for today 0s/i)).toBeTruthy();
    emit("reading");
    emit("idle");
  });
});
