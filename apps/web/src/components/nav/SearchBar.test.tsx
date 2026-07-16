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

/// Phase 3: promoting a scoped search to every room, and narrowing the result set with chips.
describe("SearchBar - search everywhere", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue(empty);
  });

  const hit = (over: Record<string, unknown>) => ({
    recordingId: "r", name: "Rec", createdAt: "2026-06-26T12:00:00Z", durationMs: 1000,
    sectionId: null, sectionName: null, breadcrumb: [], snippet: "text", snippetStartMs: 0,
    speakerName: null, score: 0.5, ...over,
  });

  it("offers Search everywhere while scoped", async () => {
    renderBar({ sectionId: "customers" });
    type("budget");
    expect(await screen.findByRole("button", { name: /search everywhere/i })).toBeTruthy();
  });

  it("promotes to every room, dropping the folder scope", async () => {
    renderBar({ sectionId: "customers" });
    type("budget");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));

    await waitFor(() =>
      expect(api.search).toHaveBeenCalledWith(expect.objectContaining({ q: "budget", everywhere: true })),
    );
  });

  it("swaps the folder chip for an Everywhere chip once promoted", async () => {
    renderBar({ sectionId: "customers" });
    type("budget");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));

    expect(await screen.findByText(/everywhere/i)).toBeTruthy();
    expect(screen.queryByText(/in customers/i)).toBeNull();
  });

  // Clearing the query is the way back to the drill, so the scope must not outlive the search that set it.
  it("drops back to the folder scope when the search is cleared", async () => {
    renderBar({ sectionId: "customers" });
    type("budget");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));
    fireEvent.click(screen.getByRole("button", { name: /clear search/i }));

    type("again");
    await waitFor(() =>
      expect(api.search).toHaveBeenLastCalledWith(expect.objectContaining({ everywhere: false })),
    );
  });

  it("groups results by folder with a count once everywhere", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "budget", scope: "everywhere", folders: [],
      recordings: [
        hit({ recordingId: "a", name: "Renewal call", sectionId: "cust", sectionName: "Customers", score: 0.9 }),
        hit({ recordingId: "b", name: "Pricing chat", sectionId: "cust", sectionName: "Customers", score: 0.8 }),
        hit({ recordingId: "c", name: "Label spec", sectionId: "pack", sectionName: "Packaging", score: 0.4 }),
      ],
    });
    renderBar({ sectionId: "customers" });
    type("budget");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));

    const customers = await screen.findByRole("heading", { name: /customers/i });
    expect(customers.textContent).toContain("2");
    expect(screen.getByRole("heading", { name: /packaging/i })).toBeTruthy();
  });

  it("filters the results by a folder chip", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "budget", scope: "everywhere", folders: [],
      recordings: [
        hit({ recordingId: "a", name: "Renewal call", sectionId: "cust", sectionName: "Customers", score: 0.9 }),
        hit({ recordingId: "c", name: "Label spec", sectionId: "pack", sectionName: "Packaging", score: 0.4 }),
      ],
    });
    renderBar({});
    type("budget");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));
    expect(await screen.findByText("Label spec")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /^section$/i }));
    fireEvent.click(await screen.findByRole("menuitem", { name: /customers/i }));

    expect(screen.getByText("Renewal call")).toBeTruthy();
    expect(screen.queryByText("Label spec")).toBeNull();
  });

  it("filters the results by a speaker chip", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "budget", scope: "everywhere", folders: [],
      recordings: [
        hit({ recordingId: "a", name: "Alice one", speakerName: "Alice", score: 0.9 }),
        hit({ recordingId: "b", name: "Bob one", speakerName: "Bob", score: 0.4 }),
      ],
    });
    renderBar({});
    type("budget");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));
    expect(await screen.findByText("Bob one")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /^speaker$/i }));
    fireEvent.click(await screen.findByRole("menuitem", { name: /alice/i }));

    expect(screen.getByText("Alice one")).toBeTruthy();
    expect(screen.queryByText("Bob one")).toBeNull();
  });

  it("shows no filter chips while scoped to a folder", async () => {
    renderBar({ sectionId: "customers" });
    type("budget");
    await waitFor(() => expect(api.search).toHaveBeenCalled());
    expect(screen.queryByRole("button", { name: /^section$/i })).toBeNull();
  });
});

describe("SearchBar - grouped hit breadcrumbs", () => {
  beforeEach(() => vi.clearAllMocks());

  const base = {
    recordingId: "a", name: "Renewal call", createdAt: "2026-06-26T12:00:00Z", durationMs: 0,
    snippet: "text", snippetStartMs: 0, speakerName: null, score: 0.9,
  };

  // The group header already says "Customers"; repeating it on every row under it is noise.
  it("omits a hit's breadcrumb when it only repeats the group header", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "x", scope: "everywhere", folders: [],
      recordings: [{ ...base, sectionId: "cust", sectionName: "Customers", breadcrumb: ["Customers"] }],
    });
    renderBar({});
    type("x");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));
    await screen.findByRole("heading", { name: /customers/i });

    // The heading is the only "Customers" on screen - the row does not repeat it.
    expect(screen.getAllByText(/customers/i)).toHaveLength(1);
  });

  // A nested folder's parents are not in the header, so the path still earns its place.
  it("keeps the breadcrumb when it names parents the group header does not", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "x", scope: "everywhere", folders: [],
      recordings: [{ ...base, sectionId: "ambu", sectionName: "Ambu", breadcrumb: ["Customers", "Ambu"] }],
    });
    renderBar({});
    type("x");
    fireEvent.click(await screen.findByRole("button", { name: /search everywhere/i }));

    expect(await screen.findByText(/Customers › Ambu/)).toBeTruthy();
  });

  // Scoped results are a flat list with no headers, so the path is the only thing saying where a hit lives.
  it("keeps the breadcrumb in a flat scoped result", async () => {
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue({
      query: "x", scope: "folder", folders: [],
      recordings: [{ ...base, sectionId: "cust", sectionName: "Customers", breadcrumb: ["Customers"] }],
    });
    renderBar({});
    type("x");
    expect(await screen.findByText("Customers")).toBeTruthy();
  });
});

describe("SearchBar - keyboard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.search as ReturnType<typeof vi.fn>).mockResolvedValue(empty);
  });

  it("focuses the field on Ctrl-K", () => {
    renderBar({});
    fireEvent.keyDown(window, { key: "k", ctrlKey: true });
    expect(document.activeElement).toBe(screen.getByRole("searchbox"));
  });

  it("focuses the field on Cmd-K", () => {
    renderBar({});
    fireEvent.keyDown(window, { key: "k", metaKey: true });
    expect(document.activeElement).toBe(screen.getByRole("searchbox"));
  });

  // A bare "k" is a character someone is typing, not a shortcut.
  it("ignores k without a modifier", () => {
    renderBar({});
    fireEvent.keyDown(window, { key: "k" });
    expect(document.activeElement).not.toBe(screen.getByRole("searchbox"));
  });

  it("does not steal the browser's own Ctrl-K when already typing in the field", () => {
    const onQueryChange = renderBar({});
    type("budget");
    fireEvent.keyDown(window, { key: "k", ctrlKey: true });
    // Still focused, and the query is untouched - the shortcut is a no-op rather than a reset.
    expect((screen.getByRole("searchbox") as HTMLInputElement).value).toBe("budget");
    expect(onQueryChange).not.toHaveBeenLastCalledWith("");
  });
});
