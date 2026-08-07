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
});
