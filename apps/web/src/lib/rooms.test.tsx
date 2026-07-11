import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { RoomProvider, useRoom } from "./rooms";
import { RoomPermission, type RoomListItem } from "./types";
import { api } from "./api";

vi.mock("./api", () => ({ api: { listRooms: vi.fn(), getUserSettings: vi.fn() } }));

const defaultSettings = { placementMode: "SelectedFolder", placementSectionId: null };

const personal: RoomListItem = {
  id: "p1",
  name: "Ada Lovelace",
  kind: 0,
  icon: null,
  color: null,
  isPersonal: true,
  permissions: RoomPermission.CreateRecording | RoomPermission.ManageRoom,
};

function Harness() {
  const { currentRoom, can, recordingSectionId } = useRoom();
  return (
    <div>
      <span data-testid="room">{currentRoom?.name ?? "-"}</span>
      <span data-testid="personal">{String(currentRoom?.isPersonal ?? false)}</span>
      <span data-testid="create">{String(can(RoomPermission.CreateRecording))}</span>
      <span data-testid="manage">{String(can(RoomPermission.ManageRoom))}</span>
      <span data-testid="rec-section">{recordingSectionId ?? "null"}</span>
    </div>
  );
}

const shared: RoomListItem = {
  id: "s1",
  name: "Engineering",
  kind: 1,
  icon: null,
  color: "#123456",
  isPersonal: false,
  permissions: RoomPermission.CreateRecording,
};

function renderHarness(path = "/") {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={[path]}>
        <RoomProvider>
          <Harness />
        </RoomProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("RoomProvider", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as Mock).mockResolvedValue(defaultSettings);
  });

  it("exposes the personal room and its permission grid", async () => {
    (api.listRooms as Mock).mockResolvedValue([personal]);
    renderHarness();

    await screen.findByText("Ada Lovelace"); // wait for the query to resolve
    expect(screen.getByTestId("personal").textContent).toBe("true");
    expect(screen.getByTestId("create").textContent).toBe("true");
    expect(screen.getByTestId("manage").textContent).toBe("true");
  });

  it("fails closed: can() is false when the current room grants nothing", async () => {
    (api.listRooms as Mock).mockResolvedValue([{ ...personal, permissions: 0 }]);
    renderHarness();

    await screen.findByText("Ada Lovelace"); // wait for the query to resolve
    expect(screen.getByTestId("create").textContent).toBe("false");
    expect(screen.getByTestId("manage").textContent).toBe("false");
  });

  it("picks the room named in the /rooms/:roomId URL, not the personal default", async () => {
    (api.listRooms as Mock).mockResolvedValue([personal, shared]);
    renderHarness("/rooms/s1/recordings/abc");

    await screen.findByText("Engineering");
    expect(screen.getByTestId("personal").textContent).toBe("false");
    // Engineering grants only CreateRecording, not ManageRoom.
    expect(screen.getByTestId("create").textContent).toBe("true");
    expect(screen.getByTestId("manage").textContent).toBe("false");
  });

  it("resolves recordingSectionId to the viewed folder in SelectedFolder mode", async () => {
    (api.listRooms as Mock).mockResolvedValue([personal]);
    (api.getUserSettings as Mock).mockResolvedValue({ placementMode: "SelectedFolder", placementSectionId: null });
    renderHarness("/sections/sec-9");

    await screen.findByText("Ada Lovelace");
    expect(screen.getByTestId("rec-section").textContent).toBe("sec-9");
  });

  it("resolves recordingSectionId to null in Ungrouped mode, even on a folder page", async () => {
    (api.listRooms as Mock).mockResolvedValue([personal]);
    (api.getUserSettings as Mock).mockResolvedValue({ placementMode: "Ungrouped", placementSectionId: "sec-x" });
    renderHarness("/sections/sec-9");

    await screen.findByText("Ada Lovelace");
    expect(screen.getByTestId("rec-section").textContent).toBe("null");
  });

  it("resolves recordingSectionId to the fixed folder in SpecificFolder mode", async () => {
    (api.listRooms as Mock).mockResolvedValue([personal]);
    (api.getUserSettings as Mock).mockResolvedValue({ placementMode: "SpecificFolder", placementSectionId: "sec-fixed" });
    renderHarness("/sections/sec-9"); // viewing a different folder; the fixed one wins

    await screen.findByText("Ada Lovelace");
    expect(screen.getByTestId("rec-section").textContent).toBe("sec-fixed");
  });
});
