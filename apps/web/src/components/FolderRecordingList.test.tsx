import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));

// The current room the folder page is viewing; the component must scope its queries to it.
const roomState = { currentRoom: undefined as { id: string; isPersonal: boolean } | undefined };
vi.mock("../lib/rooms", () => ({
  useRoom: () => ({
    currentRoom: roomState.currentRoom,
    rooms: [],
    permissions: 0,
    can: () => true,
    selectedSectionId: null,
    recordingSectionId: null,
    isLoading: false,
  }),
}));

vi.mock("../lib/api", () => ({ api: { listRecordings: vi.fn(), listSections: vi.fn() } }));

import { api } from "../lib/api";
import FolderRecordingList from "./FolderRecordingList";

const mock = (fn: unknown) => fn as ReturnType<typeof vi.fn>;

const rec = {
  id: "r1", title: "Kickoff", name: null, source: "Microphone", durationMs: 1000,
  status: "Transcribed", createdAt: "2026-01-01T00:00:00Z", sectionId: "sec-1", sectionName: "Planning",
};
const section = { id: "sec-1", name: "Planning", parentId: null, position: 0 };

function renderList() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <FolderRecordingList sectionId="sec-1" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("FolderRecordingList room scoping (issue #295)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // The folder + its recording live in the SHARED room; the personal library (no roomId) is empty. So the
    // list only populates if the component scopes its queries to the current room.
    mock(api.listRecordings).mockImplementation((roomId?: string) =>
      Promise.resolve(roomId === "room-s" ? [rec] : []),
    );
    mock(api.listSections).mockImplementation((roomId?: string) =>
      Promise.resolve(roomId === "room-s" ? [section] : []),
    );
  });

  it("lists a shared-room folder's recordings, scoped to the current room (not the personal library)", async () => {
    roomState.currentRoom = { id: "room-s", isPersonal: false };
    renderList();

    await waitFor(() => expect(screen.getByText("Kickoff")).toBeTruthy());
    expect(api.listRecordings).toHaveBeenCalledWith("room-s");
    expect(api.listSections).toHaveBeenCalledWith("room-s");
  });
});
