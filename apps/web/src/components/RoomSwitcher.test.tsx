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

  // The account menu now lives at the left of this row. The switcher does not own it - it takes it as a
  // slot - so the two triggers stay separately labelled and separately hoverable.
  it("renders a leading slot before the room trigger", () => {
    render(
      <RoomSwitcher onCollapse={() => {}} chevron="◀" leading={<button type="button">ACCOUNT</button>} />,
    );
    const slot = screen.getByText("ACCOUNT");
    const roomTrigger = screen.getByRole("button", { name: /switch room/i });
    expect(slot).toBeTruthy();
    // Precedes the room trigger in document order.
    expect(slot.compareDocumentPosition(roomTrigger) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
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

  // The account menu now sits immediately to the left of this trigger, and for a personal room both would
  // render the same signed-in user's avatar - two identical faces side by side. The row carries the icon
  // once, in the account pill; the trigger is the room's name alone.
  it("shows no room icon on the trigger", () => {
    const withIcon: RoomListItem = { ...shared, icon: "star", name: "Engineering" };
    roomsValue = { rooms: [personal, withIcon], currentRoom: withIcon };
    renderSwitcher();
    const trigger = screen.getByRole("button", { name: /switch room/i });
    // A shared room's badge is an SVG glyph and a personal room's avatar is an <img> or an initials bubble;
    // none of them belong on the trigger.
    expect(trigger.querySelector("svg")).toBeNull();
    expect(trigger.querySelector("img")).toBeNull();

    roomsValue = { rooms: [personal], currentRoom: personal };
    renderSwitcher();
    const personalTrigger = screen.getAllByRole("button", { name: /switch room/i })[1];
    expect(personalTrigger.textContent).toBe("Ada Lovelace▾"); // the name and its caret, no initials bubble
  });

  // ...but the menu still needs them: that is where you tell one room from another.
  it("keeps each room's icon in the open menu", () => {
    const withIcon: RoomListItem = { ...shared, icon: "star", name: "Engineering" };
    roomsValue = { rooms: [personal, withIcon], currentRoom: personal };
    renderSwitcher();
    fireEvent.click(screen.getByRole("button", { name: /switch room/i }));
    const items = within(screen.getByRole("menu")).getAllByRole("menuitem");
    expect(items[0].textContent).toContain("AL"); // personal: the user's initials bubble
    expect(items[1].querySelector("svg")).toBeTruthy(); // shared: its chosen glyph
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
    expect(within(menu).getByText(/3 folders . 34 recordings/i)).toBeTruthy();
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
    expect(within(screen.getByRole("menu")).getByText(/1 folder . 1 recording/i)).toBeTruthy();
  });
});
