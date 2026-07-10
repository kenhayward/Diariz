import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { RoomProvider, useRoom } from "./rooms";
import { RoomPermission, type RoomListItem } from "./types";
import { api } from "./api";

vi.mock("./api", () => ({ api: { listRooms: vi.fn() } }));

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
  const { currentRoom, can } = useRoom();
  return (
    <div>
      <span data-testid="room">{currentRoom?.name ?? "-"}</span>
      <span data-testid="personal">{String(currentRoom?.isPersonal ?? false)}</span>
      <span data-testid="create">{String(can(RoomPermission.CreateRecording))}</span>
      <span data-testid="manage">{String(can(RoomPermission.ManageRoom))}</span>
    </div>
  );
}

function renderHarness() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <RoomProvider>
          <Harness />
        </RoomProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("RoomProvider", () => {
  beforeEach(() => vi.clearAllMocks());

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
});
