import { render, screen, fireEvent, within } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import RoomSwitcher from "./RoomSwitcher";
import type { RoomListItem } from "../lib/types";

const navigate = vi.fn();
vi.mock("react-router-dom", () => ({ useNavigate: () => navigate }));
vi.mock("../auth", () => ({ useAuth: () => ({ initials: "AL", pictureUrl: null }) }));

const personal: RoomListItem = {
  id: "p1", name: "Ada Lovelace", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63,
};
const shared: RoomListItem = {
  id: "s1", name: "Engineering", kind: 1, icon: null, color: "#123456", isPersonal: false, permissions: 2,
};

let roomsValue: { rooms: RoomListItem[]; currentRoom: RoomListItem | undefined };
vi.mock("../lib/rooms", () => ({ useRoom: () => roomsValue }));

function renderSwitcher() {
  return render(<RoomSwitcher onCollapse={() => {}} chevron="◀" />);
}

describe("RoomSwitcher", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    roomsValue = { rooms: [personal, shared], currentRoom: personal };
  });

  it("shows the current room's name and opens a menu listing every room", () => {
    renderSwitcher();
    // current room name is on the trigger
    expect(screen.getAllByText("Ada Lovelace").length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    const menu = screen.getByRole("menu");
    const items = within(menu).getAllByRole("menuitem");
    // Each item also renders a room icon (avatar initials / colour glyph), so match on the name substring.
    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain("Ada Lovelace"); // personal first
    expect(items[1].textContent).toContain("Engineering");
  });

  it("navigates to a room when a different one is picked, and not when the current one is picked", () => {
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    const menu = screen.getByRole("menu");

    fireEvent.click(within(menu).getByText("Engineering"));
    expect(navigate).toHaveBeenCalledWith("/rooms/s1");

    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    fireEvent.click(within(screen.getByRole("menu")).getByText("Ada Lovelace"));
    expect(navigate).toHaveBeenCalledTimes(1); // picking the current room does not navigate
  });
});
