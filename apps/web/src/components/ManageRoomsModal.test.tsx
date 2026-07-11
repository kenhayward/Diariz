import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";
import ManageRoomsModal from "./ManageRoomsModal";
import { api } from "../lib/api";

vi.mock("../lib/api", () => ({
  api: {
    listRooms: vi.fn(),
    listUsers: vi.fn(),
    listGroups: vi.fn(),
    getRoom: vi.fn(),
    createRoom: vi.fn(),
    updateRoom: vi.fn(),
    deleteRoom: vi.fn(),
    setRoomMember: vi.fn(),
    removeRoomMember: vi.fn(),
  },
}));

const personal = { id: "p1", name: "Ada", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63 };
const eng = { id: "r1", name: "Engineering", kind: 1, icon: "users", color: "#123456", isPersonal: false, permissions: 63 };

function renderModal() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ManageRoomsModal onClose={() => {}} />
    </QueryClientProvider>,
  );
}

describe("ManageRoomsModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listRooms as Mock).mockResolvedValue([personal, eng]);
    (api.listUsers as Mock).mockResolvedValue([{ id: "u1", email: "u1@x.test", fullName: "Grace" }]);
    (api.listGroups as Mock).mockResolvedValue([]);
    (api.getRoom as Mock).mockResolvedValue({
      id: "r1", name: "Engineering", description: null, icon: "users", color: "#123456", members: [],
    });
    (api.createRoom as Mock).mockResolvedValue({ id: "r2" });
    (api.updateRoom as Mock).mockResolvedValue(undefined);
    (api.deleteRoom as Mock).mockResolvedValue(undefined);
    (api.setRoomMember as Mock).mockResolvedValue(undefined);
  });

  it("lists only shared rooms and edits the selected one's name", async () => {
    renderModal();
    const eng1 = await screen.findByRole("button", { name: "Engineering" });
    expect(screen.queryByRole("button", { name: "Ada" })).toBeNull(); // personal room is not manageable here

    fireEvent.click(eng1);
    const nameInput = await screen.findByDisplayValue("Engineering");
    fireEvent.change(nameInput, { target: { value: "Platform" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateRoom).toHaveBeenCalledWith("r1", expect.objectContaining({ name: "Platform" })),
    );
  });

  it("creates a room via New Room", async () => {
    renderModal();
    await screen.findByRole("button", { name: "Engineering" });
    fireEvent.click(screen.getByRole("button", { name: /new room/i }));
    await waitFor(() => expect(api.createRoom).toHaveBeenCalled());
  });

  it("retries with the next number when the server already has that room name (409)", async () => {
    // The member-scoped list is empty, but the server has an orphan "Room 2" (a room we're not in) → 409,
    // then "Room 3" succeeds.
    (api.listRooms as Mock).mockResolvedValue([personal]); // no shared rooms visible → first try is "Room 1"
    (api.createRoom as Mock)
      .mockRejectedValueOnce({ response: { status: 409 } })
      .mockResolvedValueOnce({ id: "r2" });
    renderModal();
    await screen.findByRole("button", { name: /new room/i });
    fireEvent.click(screen.getByRole("button", { name: /new room/i }));

    await waitFor(() => expect(api.createRoom).toHaveBeenCalledTimes(2));
    expect((api.createRoom as Mock).mock.calls[0][0]).toEqual({ name: "Room 1" });
    expect((api.createRoom as Mock).mock.calls[1][0]).toEqual({ name: "Room 2" });
  });

  it("adds a member with default CreateRecording", async () => {
    renderModal();
    fireEvent.click(await screen.findByRole("button", { name: "Engineering" }));
    await screen.findByDisplayValue("Engineering");

    fireEvent.change(screen.getByLabelText(/add a member/i), { target: { value: "0:u1" } });
    await waitFor(() =>
      expect(api.setRoomMember).toHaveBeenCalledWith("r1", { principalType: 0, principalId: "u1", permissions: 2 }),
    );
  });

  it("requires the room name typed before delete is enabled", async () => {
    renderModal();
    fireEvent.click(await screen.findByRole("button", { name: "Engineering" }));
    await screen.findByDisplayValue("Engineering");

    const del = screen.getByRole("button", { name: /delete room/i });
    expect((del as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(screen.getByPlaceholderText("Engineering"), { target: { value: "Engineering" } });
    expect((del as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(del);
    await waitFor(() => expect(api.deleteRoom).toHaveBeenCalledWith("r1"));
  });
});
