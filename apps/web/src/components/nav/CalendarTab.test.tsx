import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { useState } from "react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { SelectionProvider } from "../../lib/selection";
import type { RecordingSummary } from "../../lib/types";

// Only what this leaf reaches for - not the panel's whole mock wall. Follows the
// RecordingDetail.speakers.test.tsx precedent: test an extracted leaf with its own minimal preamble.
vi.mock("../../lib/api", () => ({
  api: { getProfile: vi.fn().mockResolvedValue(null), getCalendarEvents: vi.fn().mockResolvedValue([]) },
  apiErrorMessage: (e: unknown) => String(e),
}));
vi.mock("../../lib/rooms", () => ({
  useRoomBasePath: () => "",
  useSharedRoomId: () => undefined,
}));

import { api } from "../../lib/api";
import CalendarTab from "./CalendarTab";

const today = new Date();

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
};

/// The tab behind a toggle, mirroring how the panel mounts it: switching away unmounts it. `SelectionProvider`
/// stays up either way, as it does in the real tree.
function renderTab(recordings: RecordingSummary[] = [rec]) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  function Harness() {
    const [open, setOpen] = useState(true);
    return (
      <>
        <button onClick={() => setOpen((o) => !o)}>toggle-tab</button>
        {open && <CalendarTab recordings={recordings} isPersonalRoom />}
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

describe("CalendarTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getProfile as Mock).mockResolvedValue(null);
    (api.getCalendarEvents as Mock).mockResolvedValue([]);
  });

  it("opens on today's month and lists that day's recordings", async () => {
    renderTab();
    expect(await screen.findByText("Today call")).toBeTruthy();
    expect(screen.getByText(new RegExp(monthName(0), "i"))).toBeTruthy();
  });

  // The tab now unmounts when you switch away, which is what stops its two queries running while you are
  // reading the list. The month goes with it: leaving the Calendar and coming back starts on the current
  // month rather than wherever you had browsed to. Deliberate - the alternative is keeping cold state alive
  // in the panel for a tab that may never be opened - but it IS a behaviour change, so it is pinned here.
  it("starts on the current month again after being left and reopened", async () => {
    renderTab();
    await screen.findByText("Today call");

    fireEvent.click(screen.getByRole("button", { name: /next month/i }));
    await waitFor(() => expect(screen.getByText(new RegExp(monthName(1), "i"))).toBeTruthy());

    fireEvent.click(screen.getByText("toggle-tab")); // leave the tab
    fireEvent.click(screen.getByText("toggle-tab")); // come back

    expect(await screen.findByText(new RegExp(monthName(0), "i"))).toBeTruthy();
  });

  it("does not fetch Google events until the user has connected Calendar", async () => {
    renderTab();
    await screen.findByText("Today call");
    expect(api.getCalendarEvents).not.toHaveBeenCalled();
  });

  /// Render at an explicit room scope, for the pair of overlay tests below.
  function renderAtScope(isPersonalRoom: boolean) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
      <QueryClientProvider client={qc}>
        <SelectionProvider>
          <MemoryRouter initialEntries={["/"]}>
            <CalendarTab recordings={[rec]} isPersonalRoom={isPersonalRoom} />
          </MemoryRouter>
        </SelectionProvider>
      </QueryClientProvider>,
    );
  }

  const connected = () => {
    (api.getProfile as Mock).mockResolvedValue({ googleCalendar: true });
    (api.getCalendarEvents as Mock).mockResolvedValue([
      { id: "e1", summary: "Standup", start: today.toISOString(), end: today.toISOString() },
    ]);
  };

  // This pair has to be read together. On its own, "a shared room does not fetch" is a test that cannot
  // fail: the fetch is gated behind the profile query resolving, so asserting not-called too early passes
  // whatever the gate says (the panel-suite case this was ported from had exactly that hole - dropping
  // `&& isPersonalRoom` from the query left it green). The personal-room case below establishes that the
  // fetch does happen, and how long it takes to happen, so the shared-room case can wait past that point
  // before asserting it did not.
  it("fetches Google events in a personal room once Calendar is connected", async () => {
    connected();
    renderAtScope(true);
    await waitFor(() => expect(api.getCalendarEvents).toHaveBeenCalled());
    expect(await screen.findByRole("button", { name: /refresh events/i })).toBeTruthy();
  });

  it("shows no Google overlay in a shared room, even with Calendar connected", async () => {
    connected();
    renderAtScope(false);

    expect(await screen.findByText("Today call")).toBeTruthy(); // the room's own recording still shows
    // Wait past the point the personal-room case proves the fetch fires: the profile has resolved and the
    // events query has had its chance. Only then is "not called" a claim about the gate.
    await waitFor(() => expect(api.getProfile).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByRole("button", { name: /refresh events/i })).toBeNull());
    await new Promise((r) => setTimeout(r, 20));

    expect(api.getCalendarEvents).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: /refresh events/i })).toBeNull();
  });
});
