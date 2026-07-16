import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../../lib/api", () => ({
  api: { search: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import SearchBar from "./SearchBar";

const empty = { query: "", scope: "folder", folders: [], recordings: [] };

function renderBar(props: Partial<React.ComponentProps<typeof SearchBar>> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onQueryChange = props.onQueryChange ?? vi.fn();
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <SearchBar
          roomId="r1"
          sectionId={null}
          scopeName="Customers"
          onQueryChange={onQueryChange}
          {...props}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return onQueryChange;
}

const type = (value: string) => fireEvent.change(screen.getByRole("searchbox"), { target: { value } });

describe("SearchBar", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue(empty);
  });

  it("shows the folder scope chip while drilled into a folder", () => {
    renderBar({ sectionId: "customers" });
    expect(screen.getByText(/in customers/i)).toBeTruthy();
  });

  // At the room's top level the scope is the room, which is what people already assume "search" means - and
  // a room name like "Platform Administrator" in a chip squeezes the input down to nothing (57px, measured).
  // So the chip earns its place only when the scope is a folder, i.e. genuinely worth saying.
  it("shows no scope chip at the room's top level", () => {
    renderBar({ sectionId: null, scopeName: "Platform Administrator" });
    expect(screen.queryByText(/in platform administrator/i)).toBeNull();
  });

  // Typing is what takes the list body over; the panel needs to know.
  it("reports the query so the panel can swap the list for results", async () => {
    const onQueryChange = renderBar();
    type("budget");
    await waitFor(() => expect(onQueryChange).toHaveBeenCalledWith("budget"));
  });

  it("does not search until there is a query", () => {
    renderBar();
    expect(api.search).not.toHaveBeenCalled();
  });

  it("searches the current folder by default", async () => {
    renderBar({ sectionId: "customers" });
    type("budget");
    await waitFor(() =>
      expect(api.search).toHaveBeenCalledWith(
        expect.objectContaining({ q: "budget", sectionId: "customers", everywhere: false }),
      ),
    );
  });

  it("clearing restores the drill by reporting an empty query", async () => {
    const onQueryChange = renderBar({ sectionId: "customers" });
    type("budget");
    await waitFor(() => expect(api.search).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: /clear search/i }));
    expect((screen.getByRole("searchbox") as HTMLInputElement).value).toBe("");
    expect(onQueryChange).toHaveBeenLastCalledWith("");
  });

  it("Escape clears the search", async () => {
    const onQueryChange = renderBar();
    type("budget");
    fireEvent.keyDown(screen.getByRole("searchbox"), { key: "Escape" });
    expect((screen.getByRole("searchbox") as HTMLInputElement).value).toBe("");
    expect(onQueryChange).toHaveBeenLastCalledWith("");
  });

  it("renders a recording hit with its snippet and folder path", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "budget",
      scope: "folder",
      folders: [],
      recordings: [
        {
          recordingId: "rec-1",
          name: "Quarterly review",
          createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
          durationMs: 9000,
          sectionId: "ambu",
          sectionName: "Ambu",
          breadcrumb: ["Customers", "Ambu"],
          snippet: "we cut the budget in half",
          snippetStartMs: 4000,
          speakerName: "Alice",
          score: 0.9,
        },
      ],
    });
    renderBar();
    type("budget");

    expect(await screen.findByText("Quarterly review")).toBeTruthy();
    expect(screen.getByText(/we cut the/i)).toBeTruthy();
    expect(screen.getByText(/Customers › Ambu/)).toBeTruthy();
  });

  // The snippet is plain text and the query is highlighted client-side - the server never ships markup.
  it("highlights the matched term in the snippet", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "budget",
      scope: "folder",
      folders: [],
      recordings: [
        {
          recordingId: "rec-1", name: "R", createdAt: new Date().toISOString(), durationMs: 0,
          sectionId: null, sectionName: null, breadcrumb: [],
          snippet: "we cut the budget in half", snippetStartMs: 0, speakerName: null, score: 0.5,
        },
      ],
    });
    renderBar();
    type("budget");

    const mark = await screen.findByText("budget", { selector: "mark" });
    expect(mark).toBeTruthy();
  });

  it("links a hit to the moment it was said", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "budget", scope: "folder", folders: [],
      recordings: [
        {
          recordingId: "rec-1", name: "Quarterly review", createdAt: new Date().toISOString(), durationMs: 0,
          sectionId: null, sectionName: null, breadcrumb: [],
          snippet: "budget", snippetStartMs: 4000, speakerName: null, score: 0.5,
        },
      ],
    });
    renderBar();
    type("budget");

    const link = await screen.findByRole("link", { name: /quarterly review/i });
    expect(link.getAttribute("href")).toContain("/recordings/rec-1");
    expect(link.getAttribute("href")).toContain("ts=4000");
  });

  it("renders a folder hit that drills rather than opening a transcript", async () => {
    const onDrill = vi.fn();
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "cust", scope: "folder", recordings: [],
      folders: [
        { id: "customers", name: "Customers", parentId: null, roomId: "r1", roomName: "Personal", breadcrumb: [], recordingCount: 41 },
      ],
    });
    renderBar({ onDrill });
    type("cust");

    fireEvent.click(await screen.findByRole("button", { name: /open customers/i }));
    expect(onDrill).toHaveBeenCalledWith("customers");
  });

  it("says so when nothing matched", async () => {
    renderBar();
    type("nothing here");
    expect(await screen.findByText(/no matches/i)).toBeTruthy();
  });
});
