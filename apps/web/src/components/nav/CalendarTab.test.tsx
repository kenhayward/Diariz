import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { useState } from "react";
import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from "vitest";
import { SelectionProvider } from "../../lib/selection";
import { dayKey } from "../../lib/calendar";
import type { CalendarEvent, RecordingSummary } from "../../lib/types";

// Only what this leaf reaches for - not the panel's whole mock wall. Follows the
// RecordingDetail.speakers.test.tsx precedent: test an extracted leaf with its own minimal preamble.
vi.mock("../../lib/api", () => ({
  api: {
    getUserSettings: vi.fn().mockResolvedValue({ outlookSyncEnabled: false }),
    getCalendarEvents: vi.fn().mockResolvedValue([]),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));
vi.mock("../../lib/rooms", () => ({
  useRoomBasePath: () => "",
  useSharedRoomId: () => undefined,
}));

import { api } from "../../lib/api";
import CalendarTab from "./CalendarTab";

const today = new Date();

/// Today at a fixed local hour. The day view places blocks by the clock, so the fixtures need a known one -
/// "now" would move every assertion about a block's top with the wall clock.
const at = (hour: number, minute = 0) =>
  new Date(today.getFullYear(), today.getMonth(), today.getDate(), hour, minute);

const rec: RecordingSummary = {
  id: "today",
  title: "Mic",
  name: "Today call",
  source: "Microphone",
  durationMs: 9000,
  status: "Transcribed",
  createdAt: today.toISOString(),
  sectionId: null,
  sectionName: null,
  hasActions: false,
  hasAudio: true,
  calendarEventId: null,
  startedAt: at(10).toISOString(),
  endedAt: at(10, 30).toISOString(),
};

/// The tab behind a toggle, mirroring how the panel mounts it: switching away unmounts it. `SelectionProvider`
/// stays up either way, as it does in the real tree.
function renderTab(recordings: RecordingSummary[] = [rec]) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  function Harness() {
    const [open, setOpen] = useState(true);
    // Owned here because the real parent owns it: the panel holds the selection so its toolbar can read
    // the day the quick sync should refresh.
    const [selectedDay, setSelectedDay] = useState<string | null>(() => dayKey(new Date()));
    return (
      <>
        <button onClick={() => setOpen((o) => !o)}>toggle-tab</button>
        {open && <CalendarTab recordings={recordings} isPersonalRoom selectedDay={selectedDay} onSelectDay={setSelectedDay} />}
      </>
    );
  }
  return render(
    <QueryClientProvider client={qc}>
      <SelectionProvider>
        <MemoryRouter initialEntries={["/"]}>
          <Harness />
        </MemoryRouter>
      </SelectionProvider>
    </QueryClientProvider>,
  );
}

const monthName = (offset: number) =>
  new Date(today.getFullYear(), today.getMonth() + offset, 1).toLocaleString("en", { month: "long" });

/// The month grid's own heading ("August 2026"). The year matters: the day heading below the grid names
/// the same month ("Saturday 8 August"), so a bare month name matches two elements.
const monthHeading = (offset: number) => new RegExp(`${monthName(offset)}\\s+\\d{4}`, "i");

describe("CalendarTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: false });
    (api.getCalendarEvents as Mock).mockResolvedValue([]);
    delete (window as { diariz?: unknown }).diariz;
  });

  it("opens on today's month and lists that day's recordings", async () => {
    renderTab();
    expect(await screen.findByText("Today call")).toBeTruthy();
    expect(screen.getByText(monthHeading(0))).toBeTruthy();
  });

  // The tab now unmounts when you switch away, which is what stops its two queries running while you are
  // reading the list. The month goes with it: leaving the Calendar and coming back starts on the current
  // month rather than wherever you had browsed to. Deliberate - the alternative is keeping cold state alive
  // in the panel for a tab that may never be opened - but it IS a behaviour change, so it is pinned here.
  it("starts on the current month again after being left and reopened", async () => {
    renderTab();
    await screen.findByText("Today call");

    fireEvent.click(screen.getByRole("button", { name: /next month/i }));
    await waitFor(() => expect(screen.getByText(monthHeading(1))).toBeTruthy());

    fireEvent.click(screen.getByText("toggle-tab")); // leave the tab
    fireEvent.click(screen.getByText("toggle-tab")); // come back

    expect(await screen.findByText(monthHeading(0))).toBeTruthy();
  });

  // The overlay used to be gated on a Google connection, which left anyone whose calendar was entirely .ics
  // feeds - or a desktop Outlook mirror - with a permanently empty Calendar tab. The endpoint already returns
  // [] when nothing is connected, so the gate bought nothing and cost those users the feature.
  it("fetches events without a Google connection, so a feeds-only user still gets an overlay", async () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([
      { id: "ics:1:a", summary: "Team sync", start: at(14).toISOString(), end: at(15).toISOString() },
    ]);
    renderTab();

    await screen.findByText("Today call");
    await waitFor(() => expect(api.getCalendarEvents).toHaveBeenCalled());
    expect(await screen.findByText("Team sync")).toBeTruthy();
  });

  /// Render at an explicit room scope, for the pair of overlay tests below.
  function renderAtScope(isPersonalRoom: boolean) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
      <QueryClientProvider client={qc}>
        <SelectionProvider>
          <MemoryRouter initialEntries={["/"]}>
            <CalendarTab recordings={[rec]} isPersonalRoom={isPersonalRoom} selectedDay={dayKey(new Date())} onSelectDay={() => {}} />
          </MemoryRouter>
        </SelectionProvider>
      </QueryClientProvider>,
    );
  }

  const withEvents = () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([
      { id: "e1", summary: "Standup", start: at(14).toISOString(), end: at(15).toISOString() },
    ]);
  };

  // This pair has to be read together. On its own, "a shared room does not fetch" is a test that cannot
  // fail: asserting not-called too early passes whatever the gate says (the panel-suite case this was ported
  // from had exactly that hole - dropping `&& isPersonalRoom` from the query left it green). The personal-room
  // case below establishes that the fetch does happen, and how long it takes to happen, so the shared-room
  // case can wait past that point before asserting it did not.
  it("fetches events in a personal room", async () => {
    withEvents();
    renderAtScope(true);
    await waitFor(() => expect(api.getCalendarEvents).toHaveBeenCalled());
    expect(await screen.findByText("Standup")).toBeTruthy();
  });

  it("shows no event overlay in a shared room", async () => {
    withEvents();
    // Establish the fetch - and how long it takes to land - in this same test before asserting it does not
    // happen. Asserting "not called" on its own is a test that cannot fail: it passes whatever the gate says,
    // which is exactly the hole the panel-suite version of this had (dropping `&& isPersonalRoom` from the
    // query left it green).
    const personal = renderAtScope(true);
    await screen.findByText("Standup");
    personal.unmount();
    (api.getCalendarEvents as Mock).mockClear();

    renderAtScope(false);

    expect(await screen.findByText("Today call")).toBeTruthy(); // the room's own recording still shows
    await new Promise((r) => setTimeout(r, 20));
    expect(api.getCalendarEvents).not.toHaveBeenCalled();
    expect(screen.queryByText("Standup")).toBeNull();
  });

  // ---- filling the day in after a sync ----
  //
  // The sync buttons themselves live in the panel toolbar now (ListToolbar.test.tsx covers them), and they
  // refresh the calendar when their own run completes. What is left here is every sync this tab did not
  // start - the tray's and the one that runs on launch - which must still fill the day in on their own.

  /// A desktop shell whose sync phase the test drives. The real shell pushes state changes and replays
  /// nothing on subscribe, so `emit` is the only way the tab learns a sync started or finished.
  function fakeShell() {
    const listeners: ((s: { phase: string }) => void)[] = [];
    (window as { diariz?: unknown }).diariz = {
      canSyncOutlook: true,
      outlookAvailable: vi.fn().mockResolvedValue(true),
      syncOutlookNow: vi.fn().mockResolvedValue({ started: true }),
      onOutlookState: (cb: (s: { phase: string }) => void) => {
        listeners.push(cb);
        return () => listeners.splice(listeners.indexOf(cb), 1);
      },
    };
    const emit = (phase: string) => act(() => listeners.forEach((cb) => cb({ phase })));
    return { emit };
  }

  /// Long enough for a refetch triggered by the phase change itself to have landed, so the "not yet"
  /// assertion below is about the trigger and not about how quickly the test looked.
  const settle = () => act(() => new Promise((r) => setTimeout(r, 20)));

  it("refreshes the day's events when a sync finishes, not when it starts", async () => {
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: true });
    const { emit } = fakeShell();
    renderTab();

    await waitFor(() => expect(api.getCalendarEvents).toHaveBeenCalled());
    (api.getCalendarEvents as Mock).mockClear();

    // Mid-sync the server does not have the new meetings yet, so refetching here would only redraw the
    // ones already on screen - and leave the day stale once the upload actually lands.
    emit("reading");
    emit("pushing");
    await settle();
    expect(api.getCalendarEvents).not.toHaveBeenCalled();

    // The shell drops back to idle only once the renderer has POSTed the harvested window, so this is the
    // first moment the new meetings exist to be fetched.
    emit("idle");
    await waitFor(() => expect(api.getCalendarEvents).toHaveBeenCalled());
  });

  // The day heading counts calendar *events* - holidays, birthdays and out-of-office days are all in here,
  // and none of them is a meeting.
  it("counts the day's items as events, not meetings", async () => {
    const today = new Date();
    const at = (h: number) => new Date(today.getFullYear(), today.getMonth(), today.getDate(), h).toISOString();
    vi.mocked(api.getCalendarEvents).mockResolvedValue([
      { id: "ev1", summary: "Standup", start: at(9), end: at(10), htmlLink: null },
      { id: "ev2", summary: "Review", start: at(11), end: at(12), htmlLink: null },
    ]);

    renderTab();

    expect(await screen.findByText(/2 events/)).toBeTruthy();
    expect(screen.queryByText(/meeting/i)).toBeNull();
  });

  // The grid draws a linked event as its recording row, not as an event chip - but it is still an event on
  // the calendar that day, and a heading that says "1 event" when six meetings happened is simply wrong.
  it("counts every event on the day, including ones that already have a recording", async () => {
    const today = new Date();
    const at = (h: number) => new Date(today.getFullYear(), today.getMonth(), today.getDate(), h).toISOString();
    vi.mocked(api.getCalendarEvents).mockResolvedValue([
      { id: "linked", summary: "Standup", start: at(9), end: at(10), htmlLink: null },
      { id: "loose", summary: "Review", start: at(11), end: at(12), htmlLink: null },
    ]);

    renderTab([{ ...rec, calendarEventId: "linked" }]);

    expect(await screen.findByText(/2 events/)).toBeTruthy();
  });
});

// ---- the day view: a time grid, not a list ----

/// A calendar event as the events query returns it.
const event = (id: string, start: Date, end: Date, over: Partial<CalendarEvent> = {}): CalendarEvent => ({
  id, summary: id, start: start.toISOString(), end: end.toISOString(), htmlLink: null, ...over,
});

const blocks = () => screen.getAllByTestId("day-block");

describe("CalendarTab day grid", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: false });
    (api.getCalendarEvents as Mock).mockResolvedValue([]);
    delete (window as { diariz?: unknown }).diariz;
  });

  it("draws an hour axis over the whole day window", async () => {
    renderTab();
    await screen.findByText("Today call");

    expect(screen.getAllByTestId("hour-row")).toHaveLength(17); // 06:00-23:00
    expect(screen.getByText(/^8\s?AM$/i)).toBeTruthy();
  });

  it("places a recording at the hour it started and sizes it by its length", async () => {
    renderTab();
    await screen.findByText("Today call");

    const block = blocks()[0];
    expect(block.style.top).toBe("176px"); // (10:00 - 06:00) * 44
    expect(block.style.height).toBe("22px"); // a 30-minute meeting
  });

  // The block is too short for two lines, so the title and time collapse onto one. The title must stay its
  // own text node - a `${title} - ${time}` template would make every name unfindable by text.
  it("collapses a short block to one line without welding the title to the time", async () => {
    renderTab();
    expect(await screen.findByText("Today call")).toBeTruthy();
    expect(blocks()[0].textContent).toMatch(/Today call.*10/);
  });

  it("puts two overlapping meetings side by side", async () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([
      event("Standup", at(14), at(15)),
      event("Review", at(14, 30), at(15, 30)),
    ]);
    renderTab([]);
    await screen.findByText("Standup");

    const [first, second] = blocks();
    expect(first.style.left).toBe("0%");
    expect(first.style.width).toBe("49%");
    expect(second.style.left).toBe("51%");
    expect(second.style.width).toBe("49%");
  });

  it("gives a meeting with nothing beside it the full width", async () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([event("Standup", at(14), at(15))]);
    renderTab([]);
    await screen.findByText("Standup");

    expect(blocks()[0].style.width).toBe("100%");
  });

  // A meeting that began yesterday has no meaningful start on this day, so it is pinned above the axis
  // rather than being drawn as a block at midnight.
  it("pins a spilled-in multi-day meeting above the grid", async () => {
    const yesterday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1, 22, 0);
    (api.getCalendarEvents as Mock).mockResolvedValue([event("Offsite", yesterday, at(23, 59))]);
    renderTab([]);

    const chip = await screen.findByText("Offsite");
    expect(screen.getByTestId("all-day-row").contains(chip)).toBe(true);
    expect(screen.queryAllByTestId("day-block")).toHaveLength(0);
  });

  // The axis is the point of the view - an empty day should still show you where the gaps are, rather than
  // replacing the whole grid with a sentence.
  it("keeps the axis visible on an empty day", async () => {
    renderTab([]);

    expect(await screen.findByText(/nothing on this day/i)).toBeTruthy();
    expect(screen.getAllByTestId("hour-row")).toHaveLength(17);
  });

  it("heads the day with its date and what is on it", async () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([
      event("Standup", at(14), at(15)),
      event("Review", at(16), at(17)),
    ]);
    renderTab();

    expect(await screen.findByText(/2 events/)).toBeTruthy();
    expect(screen.getByText(/1 recording/)).toBeTruthy();
  });

  it("offers a recording's actions from its block", async () => {
    renderTab();
    await screen.findByText("Today call");

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    expect(screen.getByRole("menuitem", { name: /rename/i })).toBeTruthy();
  });

  // A meeting has no menu: clicking the block already opens the event, so a one-item kebab would only
  // repeat it.
  it("gives a meeting block no kebab", async () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([event("Standup", at(14), at(15))]);
    renderTab([]);
    await screen.findByText("Standup");

    expect(screen.queryByRole("button", { name: "Actions" })).toBeNull();
  });

  it("keeps a recording block a drag source", async () => {
    renderTab();
    await screen.findByText("Today call");

    const block = blocks()[0];
    expect(block.getAttribute("draggable")).toBe("true");
    const setData = vi.fn();
    fireEvent.dragStart(block, { dataTransfer: { setData, effectAllowed: "" } });
    expect(setData).toHaveBeenCalledWith("text/plain", "today");
  });

  // A feed meeting has no Google event behind it, so there is nothing to open - it stays a static block,
  // exactly as its row did.
  it("does not link a meeting from an external feed", async () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([
      event("Team sync", at(14), at(15), { calendarId: "ics:1" }),
    ]);
    renderTab([]);
    await screen.findByText("Team sync");

    expect(screen.queryByRole("link", { name: /team sync/i })).toBeNull();
  });

  it("links a meeting to its event preview", async () => {
    (api.getCalendarEvents as Mock).mockResolvedValue([event("Standup", at(14), at(15))]);
    renderTab([]);

    const link = await screen.findByRole("link", { name: /standup/i });
    expect(link.getAttribute("href")).toBe("/calendar-event/Standup");
  });
});

// jsdom has no layout, so `scrollTop` is an accessor that clamps every write back to 0. Make it a plain
// property for this block only (restored after), so the grid's own scroll positioning is observable.
describe("CalendarTab day grid scroll position", () => {
  let original: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: false });
    (api.getCalendarEvents as Mock).mockResolvedValue([]);
    original = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "scrollTop");
    Object.defineProperty(HTMLElement.prototype, "scrollTop", { configurable: true, writable: true, value: 0 });
  });

  afterEach(() => {
    if (original) Object.defineProperty(HTMLElement.prototype, "scrollTop", original);
    vi.useRealTimers();
  });

  it("opens the grid at the working day rather than its first hour", async () => {
    // Before 09:00 the grid opens on the working day; the clock is pinned so the assertion does not
    // depend on when the suite happens to run.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(at(7));
    renderTab();
    await screen.findByText("Today call");

    expect(screen.getByTestId("day-scroller").scrollTop).toBe(88); // (08:00 - 06:00) * 44
  });

  // Later in the day the working-day default would leave the now line off screen, so the grid opens an
  // hour behind the clock instead.
  it("opens an hour before now once the day is under way", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(at(15));
    renderTab();
    await screen.findByText("Today call");

    expect(screen.getByTestId("day-scroller").scrollTop).toBe((14 - 6) * 44);
  });
});

describe("CalendarTab now line", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue({ outlookSyncEnabled: false });
    (api.getCalendarEvents as Mock).mockResolvedValue([]);
  });
  afterEach(() => vi.useRealTimers());

  it("marks the current time on today's grid", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(at(14));
    renderTab();
    await screen.findByText("Today call");

    expect(screen.getByTestId("now-line").style.top).toBe("352px"); // (14:00 - 06:00) * 44
  });

  it("moves the line as time passes and stops when the tab closes", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(at(14));
    const setInterval = vi.spyOn(globalThis, "setInterval");
    const clearInterval = vi.spyOn(globalThis, "clearInterval");
    const { unmount } = renderTab();
    await screen.findByText("Today call");

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10 * 60_000);
    });
    expect(screen.getByTestId("now-line").style.top).toBe(`${(14 + 10 / 60 - 6) * 44}px`);

    // The minute tick is the only one-minute interval in the tree; leaving it running would keep
    // re-rendering a grid nobody is looking at.
    const tick = setInterval.mock.calls.findIndex(([, ms]) => ms === 60_000);
    expect(tick).toBeGreaterThanOrEqual(0);
    unmount();
    expect(clearInterval).toHaveBeenCalledWith(setInterval.mock.results[tick].value);
  });

  it("shows no now line on a day that is not today", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(at(14));
    renderTab();
    await screen.findByText("Today call");

    const other = today.getDate() === 1 ? 2 : 1;
    fireEvent.click(screen.getByRole("button", { name: String(other) }));
    expect(screen.queryByTestId("now-line")).toBeNull();
  });
});
