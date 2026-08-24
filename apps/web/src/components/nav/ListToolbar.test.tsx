import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { SelectionProvider } from "../../lib/selection";
import { StatusProvider } from "../../lib/status";
import { CalendarSyncProvider } from "../../lib/calendarSync";
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

/// The toolbar as the app mounts it: inside the workspace's CalendarSyncProvider (which owns the run, so it
/// survives this toolbar), with the status bar underneath so what a sync tells the user is observable rather
/// than inferred from internals.
function renderToolbar(over: Partial<Parameters<typeof ListToolbar>[0]> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={qc}>
      <StatusProvider>
        <CalendarSyncProvider>
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
            onGoToToday={() => {}}
            {...over}
          />
          <StatusBar />
        </SelectionProvider>
        </CalendarSyncProvider>
      </StatusProvider>
    </QueryClientProvider>,
  );
  return { ...view, qc };
}

/// A desktop shell whose sync phase the test drives. It pushes state *changes*, so `emit` is how a run learns
/// the shell has finished; `phase` is what it answers when asked where it is right now, which is what a
/// subscriber arriving mid-run has to go on. Left `idle` (the default) it behaves like a shell sitting still.
function fakeShell(
  started: { started: boolean; reason?: string } = { started: true },
  phase: "idle" | "reading" | "pushing" = "idle",
) {
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
    outlookState: () => Promise.resolve({ phase }),
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

    expect(await screen.findByRole("button", { name: /sync selected day/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /sync calendar/i })).toBeTruthy();
  });

  it("offers neither on the list", () => {
    renderToolbar();

    expect(screen.queryByRole("button", { name: /sync selected day/i })).toBeNull();
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

  it("asks the desktop shell for one day only when the quick sync is pressed", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { syncOutlookNow, emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false });

    fireEvent.click(await screen.findByRole("button", { name: /sync selected day/i }));

    await waitFor(() =>
      expect(syncOutlookNow).toHaveBeenCalledWith({ scope: "today", date: undefined }),
    );
    emit("reading");
    emit("idle");
  });

  // The bug this fixes: with a day picked in the calendar, the button used to re-read TODAY and leave the
  // day on screen exactly as stale as it was.
  it("syncs the day the calendar is showing, not today", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { syncOutlookNow, emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false, selectedDay: "2026-08-20" });

    fireEvent.click(await screen.findByRole("button", { name: /sync selected day/i }));

    await waitFor(() =>
      expect(syncOutlookNow).toHaveBeenCalledWith({ scope: "today", date: "2026-08-20" }),
    );
    emit("reading");
    emit("idle");
  });

  // "Refreshing" with no date named was the other half of the problem - the message said "today" whatever
  // day was actually being read.
  it("names the day being read in the status bar", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false, selectedDay: "2026-08-20" });

    fireEvent.click(await screen.findByRole("button", { name: /sync selected day/i }));

    expect(await screen.findByText(/syncing calendar for 20th august 2026 0s/i)).toBeTruthy();
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

  // The regression this pair exists for. The old Sync Outlook link greyed itself out while the shell was
  // busy, so a sync started on launch or from the tray simply could not be clicked over. Moving the buttons
  // to the toolbar dropped that guard, and the shell answered "busy" - which was reported to the user as a
  // failure, on every launch, for the whole of the launch sync.
  it("disables both buttons while a sync it did not start is running", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false });

    const today = await screen.findByRole("button", { name: /sync selected day/i });
    const all = screen.getByRole("button", { name: /sync calendar/i });
    await waitFor(() => expect(api.getUserSettings).toHaveBeenCalled());

    emit("reading");
    await waitFor(() => expect(today).toHaveProperty("disabled", true));
    expect(all).toHaveProperty("disabled", true);

    emit("idle");
    await waitFor(() => expect(today).toHaveProperty("disabled", false));
    expect(all).toHaveProperty("disabled", false);
  });

  // The half of that regression the pair above could never catch, because it emits the phase change itself.
  // The shell pushes only *changes*, so a toolbar that mounts while a run is already reading - which is every
  // launch sync, and every remount after one starts (the Actions tab, a collapsed panel, a reload) - was told
  // nothing at all. Both buttons stayed live and the bar stayed silent for the whole read, and a quick sync
  // pressed in that window was refused by the shell and then spent the rest of the full sync waiting to join
  // it. That is the "sync today takes forever" report.
  it("disables both buttons and reports a sync that was already running when it mounted", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    fakeShell({ started: true }, "reading");
    renderToolbar({ calendarMode: true, listMode: false });

    const today = await screen.findByRole("button", { name: /sync selected day/i });
    const all = screen.getByRole("button", { name: /sync calendar/i });

    await waitFor(() => expect(today).toHaveProperty("disabled", true));
    expect(all).toHaveProperty("disabled", true);
    expect(await screen.findByText(/syncing calendar 0s/i)).toBeTruthy();
  });

  // ...and say so, rather than leaving two dead buttons and no explanation for why they will not press.
  it("reports a sync it did not start in the status bar", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false });
    await screen.findByRole("button", { name: /sync calendar/i });

    emit("reading");
    expect(await screen.findByText(/syncing calendar 0s/i)).toBeTruthy();

    emit("idle");
    await waitFor(() => expect(screen.queryByText(/syncing calendar/i)).toBeNull());
  });

  // "Could not sync the calendar" told the user nothing at all. Whatever the shell reports, the bar has to
  // carry something they can act on.
  it("names the reason when a sync cannot run", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    fakeShell({ started: false, reason: "new-outlook" });
    renderToolbar({ calendarMode: true, listMode: false });

    fireEvent.click(await screen.findByRole("button", { name: /sync calendar/i }));

    expect(await screen.findByText(/cannot reach Outlook/i)).toBeTruthy();
  });

  it("names the quick sync in the status bar, so the two are told apart", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { emit } = fakeShell();
    renderToolbar({ calendarMode: true, listMode: false });

    fireEvent.click(await screen.findByRole("button", { name: /sync selected day/i }));

    expect(await screen.findByText(/syncing calendar for .+ 0s/i)).toBeTruthy();
    emit("reading");
    emit("idle");
  });
});

/// The Calendar tab has two purpose-built refresh controls of its own; a third generic one beside them read as
/// a duplicate of the pair. It stays on the tabs where it is the only refresh there is.
describe("ListToolbar refresh button", () => {
  // Same reset as the sync block above: this is a sibling describe, so it does not inherit that one's
  // beforeEach, and a shell left installed by an earlier test would send these syncs down the desktop path
  // and hang them waiting for a phase nobody emits.
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: false });
    delete (window as { diariz?: unknown }).diariz;
  });

  it("offers Refresh on the list", async () => {
    renderToolbar({ listMode: true, calendarMode: false });

    expect(await screen.findByRole("button", { name: /^refresh$/i })).toBeTruthy();
  });

  it("hides Refresh on the Calendar tab, where the two syncs cover it", async () => {
    renderToolbar({ calendarMode: true, listMode: false });

    // The syncs are there; the generic refresh is not.
    expect(await screen.findByRole("button", { name: /sync calendar/i })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^refresh$/i })).toBeNull();
  });

  /// Refresh was also how you re-read the recordings drawn on the day grid. With it gone from this tab, a
  /// sync has to pick them up, or the calendar would lose a refresh it used to have.
  it("refreshes the recordings as well as the events, so the day grid keeps up", async () => {
    const { qc } = renderToolbar({ calendarMode: true, listMode: false });
    const invalidate = vi.spyOn(qc, "invalidateQueries");

    fireEvent.click(await screen.findByRole("button", { name: /sync calendar/i }));

    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith(expect.objectContaining({ queryKey: ["calendar-events"] })),
    );
    expect(invalidate).toHaveBeenCalledWith(expect.objectContaining({ queryKey: ["recordings"] }));
  });

  describe("go to today", () => {
    it("is offered on the Calendar tab", () => {
      renderToolbar({ listMode: false, calendarMode: true });
      expect(screen.getByRole("button", { name: "Go to today" })).toBeTruthy();
    });

    it("is not offered on the list", () => {
      renderToolbar();
      expect(screen.queryByRole("button", { name: "Go to today" })).toBeNull();
    });

    // The gating deliberately differs from the two syncs beside it: those are personal-only because the
    // calendar EVENT OVERLAY is, but a shared room's day grid still draws that room's recordings, so
    // navigating to today is meaningful there. Without this assertion, a later tidy-up that folded the
    // button into the isPersonalRoom block would silently drop it from shared rooms.
    it("is offered in a shared room, where the two syncs are not", () => {
      renderToolbar({ listMode: false, calendarMode: true, isPersonalRoom: false });
      expect(screen.getByRole("button", { name: "Go to today" })).toBeTruthy();
      expect(screen.queryByRole("button", { name: "Sync selected day" })).toBeNull();
      expect(screen.queryByRole("button", { name: "Sync calendar" })).toBeNull();
    });

    it("calls back when clicked", () => {
      const onGoToToday = vi.fn();
      renderToolbar({ listMode: false, calendarMode: true, onGoToToday });
      fireEvent.click(screen.getByRole("button", { name: "Go to today" }));
      expect(onGoToToday).toHaveBeenCalledTimes(1);
    });
  });
});
