import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { useState } from "react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { SelectionProvider } from "../../lib/selection";
import type { RecordingSummary } from "../../lib/types";

// Only what this leaf reaches for, not the panel's whole mock wall (the
// RecordingDetail.speakers.test.tsx precedent for testing an extracted leaf).
vi.mock("../../lib/api", () => ({
  api: { listTags: vi.fn().mockResolvedValue([]) },
  apiErrorMessage: (e: unknown) => String(e),
}));
vi.mock("../../lib/rooms", () => ({
  useRoomBasePath: () => "",
  useSharedRoomId: () => undefined,
}));

import { api } from "../../lib/api";
import TagsTab from "./TagsTab";

const base: RecordingSummary = {
  id: "a",
  title: "Mic",
  name: "Budget call",
  source: "Microphone",
  durationMs: 9000,
  status: "Transcribed",
  createdAt: "2026-07-01T09:30:00Z",
  sectionId: null,
  sectionName: null,
  hasActions: false,
  hasAudio: true,
  calendarEventId: null,
};
const recordings: RecordingSummary[] = [
  base,
  { ...base, id: "b", name: "Vendor call" },
  { ...base, id: "c", name: "Cloud call" },
];

const TAGS = [
  { tag: "Budget Planning", count: 3, weight: 0.9, recordingIds: ["a", "b", "c"] },
  { tag: "Vendor Selection", count: 2, weight: 0.6, recordingIds: ["b", "c"] },
  { tag: "Cloud Infra", count: 1, weight: 0.3, recordingIds: ["c"] },
];

/// The tab behind a toggle, mirroring how the panel mounts it: switching tab unmounts it.
function renderTab() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  function Harness() {
    const [open, setOpen] = useState(true);
    return (
      <>
        <button onClick={() => setOpen((o) => !o)}>toggle-tab</button>
        {open && <TagsTab recordings={recordings} roomId={undefined} />}
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

describe("TagsTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (api.listTags as Mock).mockResolvedValue(TAGS);
  });

  it("shows the cloud and filters the list to the picked tag", async () => {
    renderTab();
    fireEvent.click(await screen.findByRole("button", { name: "Cloud Infra" }));

    // Cloud Infra is only on "c", so the other two rows drop out of the list below.
    await waitFor(() => expect(screen.queryByText("Budget call")).toBeNull());
    expect(screen.getByText("Cloud call")).toBeTruthy();
  });

  // The tab unmounts when you switch away, which is what stops its query running from the other tabs. The
  // picked tag goes with it. Arguably a correction as much as a change: a held tag can go stale (the
  // recording is deleted or re-tagged), which is why the tab carries an effect to clear one that no longer
  // exists. Deliberate, so pinned here.
  it("clears the picked tag when the tab is left and reopened", async () => {
    renderTab();
    fireEvent.click(await screen.findByRole("button", { name: "Cloud Infra" }));
    await waitFor(() => expect(screen.queryByText("Budget call")).toBeNull());

    fireEvent.click(screen.getByText("toggle-tab")); // leave the tab
    fireEvent.click(screen.getByText("toggle-tab")); // come back

    // No tag picked: every recording carrying a shown tag is listed again.
    expect(await screen.findByText("Budget call")).toBeTruthy();
  });

  // Storage can be disabled outright (private browsing, a locked-down profile) and throw on access. The
  // read is in a useState initialiser, so an unguarded throw takes down this tab's whole render. Ported
  // down from the panel suite, where it no longer covers this code now that the read lives here.
  it("renders when localStorage cannot be read", async () => {
    const spy = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("storage disabled");
    });
    try {
      renderTab();
      expect(await screen.findByRole("button", { name: "Budget Planning" })).toBeTruthy();
    } finally {
      spy.mockRestore();
    }
  });

  // Ported down from the panel suite. The modal renders from inside this tab now (it used to sit in the
  // panel's modal slot), and the tag it picks has to drive the list behind it - they share one piece of
  // state, which is the whole reason the modal lives here rather than beside the panel's other modals.
  it("expands the cloud into a modal whose selection drives the list behind it", async () => {
    renderTab();
    await screen.findByRole("button", { name: "Budget Planning" });

    fireEvent.click(screen.getByRole("button", { name: /expand tag cloud/i }));
    const dialog = await screen.findByRole("dialog", { name: /tag cloud/i });

    const inModal = Array.from(dialog.querySelectorAll("button")).find((b) => b.textContent === "Cloud Infra")!;
    fireEvent.click(inModal);
    fireEvent.keyDown(document, { key: "Escape" }); // close without navigating
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

    // Cloud Infra is only on "c": the list behind the modal mirrors what was picked inside it.
    expect(screen.queryByText("Budget call")).toBeNull();
    expect(screen.getByText("Cloud call")).toBeTruthy();
  });

  // Ported down from the panel suite. The Tags list has room for a second line the dense List tab does not
  // (`showDate`), so the year proves a real date is rendered rather than just the duration.
  it("shows the transcript date on each row", async () => {
    renderTab();
    await screen.findByRole("button", { name: "Budget Planning" });
    expect(screen.getAllByText(/2026/).length).toBeGreaterThan(0);
  });

  // Ported down from the panel suite. Its own case rather than leaning on the storage-failure one below,
  // which also moves the slider - trimming is normal behaviour and should not be covered only while
  // localStorage happens to be broken.
  it("trims the cloud to the most-used tags as the count slider moves", async () => {
    renderTab();
    await screen.findByRole("button", { name: "Cloud Infra" });

    fireEvent.change(screen.getByRole("slider"), { target: { value: "1" } });

    await waitFor(() => expect(screen.queryByRole("button", { name: "Cloud Infra" })).toBeNull());
    expect(screen.getByRole("button", { name: "Budget Planning" })).toBeTruthy(); // highest count kept
  });

  // Ported down from the panel suite.
  it("shows the empty state when nothing is tagged", async () => {
    (api.listTags as Mock).mockResolvedValue([]);
    renderTab();
    expect(await screen.findByText(/no tagged meetings yet/i)).toBeTruthy();
  });

  it("keeps the count slider working when localStorage cannot be written", async () => {
    renderTab();
    await screen.findByRole("button", { name: "Budget Planning" });

    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("storage disabled");
    });
    try {
      // The count still moves for this session - it just will not be remembered next time.
      fireEvent.change(screen.getByRole("slider"), { target: { value: "1" } });
      await waitFor(() => expect(screen.queryByRole("button", { name: "Cloud Infra" })).toBeNull());
      expect(screen.getByRole("button", { name: "Budget Planning" })).toBeTruthy();
    } finally {
      spy.mockRestore();
    }
  });
});
