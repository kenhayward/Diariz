import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { IcsFeed } from "../../lib/types";

vi.mock("../../lib/api", () => ({
  api: {
    listCalendarFeeds: vi.fn(),
    createCalendarFeed: vi.fn(),
    updateCalendarFeed: vi.fn(),
    deleteCalendarFeed: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import FeedsCard from "./FeedsCard";

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

function renderCard() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <FeedsCard />
    </QueryClientProvider>,
  );
}

const mock = (fn: unknown) => fn as ReturnType<typeof vi.fn>;

/// Reveal the add/edit form, which now lives behind a disclosure - it used to occupy a third of the tab
/// while empty.
const openForm = () => fireEvent.click(screen.getByRole("button", { name: /add feed/i }));

describe("FeedsCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.listCalendarFeeds).mockResolvedValue([feed()]);
    mock(api.createCalendarFeed).mockResolvedValue(feed({ id: "f2" }));
    mock(api.updateCalendarFeed).mockResolvedValue(feed());
    mock(api.deleteCalendarFeed).mockResolvedValue(undefined);
  });

  it("lists existing feeds with their url", async () => {
    renderCard();
    expect(await screen.findByText("Team")).toBeTruthy();
    expect(screen.getByText("https://x.example.com/t.ics")).toBeTruthy();
  });

  it("keeps the add form closed until it is asked for", async () => {
    renderCard();
    await screen.findByText("Team");
    expect(screen.queryByPlaceholderText("Calendar name")).toBeNull();

    openForm();
    expect(screen.getByPlaceholderText("Calendar name")).toBeTruthy();
  });

  it("adds a feed with the entered name, url, and colour", async () => {
    renderCard();
    await screen.findByText("Team");
    openForm();

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
    renderCard();
    await screen.findByText("Team");
    openForm();

    fireEvent.change(screen.getByPlaceholderText("Calendar name"), { target: { value: "Ops" } });
    expect((screen.getByRole("button", { name: "Add" }) as HTMLButtonElement).disabled).toBe(true);
  });

  // The tick reads "Shown", but its accessible name names the feed: once three sources share one panel,
  // an unqualified "Shown" matches the Outlook machines' tick too.
  it("toggles a feed's shown state from a control that names the feed", async () => {
    renderCard();
    await screen.findByText("Team");

    fireEvent.click(screen.getByLabelText("Shown - Team"));

    await waitFor(() => expect(api.updateCalendarFeed).toHaveBeenCalledTimes(1));
    expect(api.updateCalendarFeed).toHaveBeenCalledWith("f1", expect.objectContaining({ enabled: false }));
  });

  it("removes a feed", async () => {
    renderCard();
    await screen.findByText("Team");

    fireEvent.click(screen.getByRole("button", { name: "Remove Team" }));

    await waitFor(() => expect(api.deleteCalendarFeed).toHaveBeenCalledWith("f1"));
  });

  it("surfaces a feed's last error", async () => {
    mock(api.listCalendarFeeds).mockResolvedValue([feed({ lastError: "The feed returned HTTP 404." })]);
    renderCard();
    expect(await screen.findByText(/HTTP 404/)).toBeTruthy();
  });

  it("edits a feed: Edit opens the form prefilled and saves via update", async () => {
    renderCard();
    await screen.findByText("Team");

    fireEvent.click(screen.getByRole("button", { name: "Edit Team" }));
    const nameInput = screen.getByPlaceholderText("Calendar name") as HTMLInputElement;
    expect(nameInput.value).toBe("Team");
    fireEvent.change(nameInput, { target: { value: "Team renamed" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(api.updateCalendarFeed).toHaveBeenCalledTimes(1));
    expect(api.updateCalendarFeed).toHaveBeenCalledWith("f1", expect.objectContaining({ name: "Team renamed" }));
  });

  it("counts the feeds it is showing in the card's status", async () => {
    mock(api.listCalendarFeeds).mockResolvedValue([feed(), feed({ id: "f2", name: "Ops", enabled: false })]);
    renderCard();
    await screen.findByText("Ops");
    expect(screen.getByTestId("source-status").textContent).toBe("1 shown");
  });

  it("says so when there are no feeds", async () => {
    mock(api.listCalendarFeeds).mockResolvedValue([]);
    renderCard();
    expect(await screen.findByText(/no calendar feeds yet/i)).toBeTruthy();
  });
});
