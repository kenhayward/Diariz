import { render, screen, fireEvent, within } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import RoomSwitcher from "./RoomSwitcher";
import type { RoomListItem } from "../lib/types";

const navigate = vi.fn();
vi.mock("react-router-dom", () => ({ useNavigate: () => navigate }));
// ManageRoomsModal pulls in react-query + api; stub it so the switcher test stays focused.
vi.mock("./ManageRoomsModal", () => ({ default: () => <div data-testid="manage-rooms-modal" /> }));
const authState = { initials: "AL", pictureUrl: null, permissions: { manageRooms: false, manageUsers: false, managePlatform: false } };
vi.mock("../auth", () => ({ useAuth: () => authState }));

const personal: RoomListItem = {
  id: "p1", name: "Ada Lovelace", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63,
  sectionCount: 3, recordingCount: 34,
};
const shared: RoomListItem = {
  id: "s1", name: "Engineering", kind: 1, icon: null, color: "#123456", isPersonal: false, permissions: 2,
  sectionCount: 5, recordingCount: 210,
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
    authState.permissions = { manageRooms: false, manageUsers: false, managePlatform: false };
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

  it("shows a shared room's chosen icon on the trigger, not just its first letter", () => {
    const withIcon: RoomListItem = { ...shared, icon: "star", name: "Engineering" };
    roomsValue = { rooms: [personal, withIcon], currentRoom: withIcon };
    renderSwitcher();
    // The trigger renders the room badge; with an icon set that badge is an SVG glyph.
    expect(screen.getByRole("button", { name: /switch room/i }).querySelector("svg")).toBeTruthy();
  });

  it("hides Manage Rooms from users without manageRooms", () => {
    authState.permissions = { manageRooms: false, manageUsers: false, managePlatform: false };
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    expect(within(screen.getByRole("menu")).queryByText(/manage rooms/i)).toBeNull();
  });

  it("shows Manage Rooms to holders of manageRooms and opens the modal", () => {
    authState.permissions = { manageRooms: true, manageUsers: false, managePlatform: false };
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    fireEvent.click(within(screen.getByRole("menu")).getByText(/manage rooms/i));
    expect(screen.getByTestId("manage-rooms-modal")).toBeTruthy();
  });

  // ---- The switcher's detail line + current-room marker ----

  it("shows each room's section and recording counts", () => {
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    const menu = screen.getByRole("menu");
    expect(within(menu).getByText(/3 sections . 34 recordings/i)).toBeTruthy();
  });

  // "shared" is the one thing you cannot tell from a name, and it decides who else can read what is in there.
  it("marks a shared room's count line as shared, and a personal one not", () => {
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    const items = within(screen.getByRole("menu")).getAllByRole("menuitem");
    expect(items[0].textContent).not.toMatch(/shared/i); // personal
    expect(items[1].textContent).toMatch(/shared/i);
  });

  it("marks which room is current", () => {
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    const items = within(screen.getByRole("menu")).getAllByRole("menuitem");
    expect(items[0].getAttribute("aria-current")).toBe("true");
    expect(items[1].getAttribute("aria-current")).toBeNull();
  });

  it("singularises a count of one", () => {
    roomsValue = {
      rooms: [{ ...personal, sectionCount: 1, recordingCount: 1 }],
      currentRoom: personal,
    };
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    expect(within(screen.getByRole("menu")).getByText(/1 section . 1 recording/i)).toBeTruthy();
  });
});
