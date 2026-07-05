import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { IcsFeed } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    listCalendarFeeds: vi.fn(),
    createCalendarFeed: vi.fn(),
    updateCalendarFeed: vi.fn(),
    deleteCalendarFeed: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import CalendarFeedsSection from "./CalendarFeedsSection";

const feed = (over: Partial<IcsFeed> = {}): IcsFeed => ({
  id: "f1",
  name: "Team",
  url: "https://x.example.com/t.ics",
  color: "#7986CB",
  enabled: true,
  lastFetchedAt: null,
  lastError: null,
  ...over,
});

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <CalendarFeedsSection />
    </QueryClientProvider>,
  );
}

const mock = (fn: unknown) => fn as ReturnType<typeof vi.fn>;

describe("CalendarFeedsSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.listCalendarFeeds).mockResolvedValue([feed()]);
    mock(api.createCalendarFeed).mockResolvedValue(feed({ id: "f2" }));
    mock(api.updateCalendarFeed).mockResolvedValue(feed());
    mock(api.deleteCalendarFeed).mockResolvedValue(undefined);
  });

  it("lists existing feeds", async () => {
    renderSection();
    expect(await screen.findByText("Team")).toBeTruthy();
  });

  it("adds a feed with the entered name, url, and colour", async () => {
    renderSection();
    await screen.findByText("Team");

    fireEvent.change(screen.getByPlaceholderText("Calendar name"), { target: { value: "Ops" } });
    fireEvent.change(screen.getByPlaceholderText("https://.../calendar.ics"), {
      target: { value: "https://ops.example.com/o.ics" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(api.createCalendarFeed).toHaveBeenCalledTimes(1));
    expect(api.createCalendarFeed).toHaveBeenCalledWith(
      expect.objectContaining({ name: "Ops", url: "https://ops.example.com/o.ics", enabled: true }),
    );
  });

  it("does not add when name or url is blank", async () => {
    renderSection();
    await screen.findByText("Team");
    // Only a name, no url -> Add stays disabled.
    fireEvent.change(screen.getByPlaceholderText("Calendar name"), { target: { value: "Ops" } });
    expect((screen.getByRole("button", { name: "Add" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("toggles a feed's shown state", async () => {
    renderSection();
    await screen.findByText("Team");

    fireEvent.click(screen.getByLabelText("Shown"));

    await waitFor(() => expect(api.updateCalendarFeed).toHaveBeenCalledTimes(1));
    expect(api.updateCalendarFeed).toHaveBeenCalledWith("f1", expect.objectContaining({ enabled: false }));
  });

  it("removes a feed", async () => {
    renderSection();
    await screen.findByText("Team");

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(api.deleteCalendarFeed).toHaveBeenCalledWith("f1"));
  });

  it("surfaces a feed's last error", async () => {
    mock(api.listCalendarFeeds).mockResolvedValue([feed({ lastError: "The feed returned HTTP 404." })]);
    renderSection();
    expect(await screen.findByText(/HTTP 404/)).toBeTruthy();
  });

  it("edits a feed: prefilled form saves via update", async () => {
    renderSection();
    await screen.findByText("Team");

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    const nameInput = screen.getByPlaceholderText("Calendar name") as HTMLInputElement;
    expect(nameInput.value).toBe("Team");
    fireEvent.change(nameInput, { target: { value: "Team renamed" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(api.updateCalendarFeed).toHaveBeenCalledTimes(1));
    expect(api.updateCalendarFeed).toHaveBeenCalledWith("f1", expect.objectContaining({ name: "Team renamed" }));
  });
});
