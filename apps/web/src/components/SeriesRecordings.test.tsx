import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import SeriesRecordings from "./SeriesRecordings";
import { api } from "../lib/api";

vi.mock("../lib/api", () => ({ api: { getSeriesRecordings: vi.fn() } }));

function renderIt() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <SeriesRecordings eventId="abc_20260810T090000Z" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("SeriesRecordings", () => {
  beforeEach(() => vi.mocked(api.getSeriesRecordings).mockReset());

  it("lists the earlier recordings, newest first, linking to each", async () => {
    vi.mocked(api.getSeriesRecordings).mockResolvedValue([
      { id: "r1", title: "Standup", name: "Standup 3 Aug", startsAt: "2026-08-03T09:00:00Z", endsAt: "2026-08-03T09:30:00Z" },
      { id: "r2", title: "Standup 27 Jul", name: null, startsAt: "2026-07-27T09:00:00Z", endsAt: "2026-07-27T09:30:00Z" },
    ]);

    renderIt();

    expect(await screen.findByText("Earlier recordings of this meeting")).toBeTruthy();
    const first = screen.getByRole("link", { name: /Standup 3 Aug/ });
    expect(first.getAttribute("href")).toBe("/recordings/r1");
    // name ?? title, exactly as the rest of the app labels a recording.
    expect(screen.getByRole("link", { name: /Standup 27 Jul/ })).toBeTruthy();
  });

  // A one-off, or a series never recorded before, returns []. Rendering a heading over an empty list would
  // put a permanent dead section on every non-recurring event.
  it("renders nothing at all when there is no history", async () => {
    vi.mocked(api.getSeriesRecordings).mockResolvedValue([]);
    const { container } = renderIt();
    await new Promise((r) => setTimeout(r, 0));
    expect(container.textContent).toBe("");
  });
});
