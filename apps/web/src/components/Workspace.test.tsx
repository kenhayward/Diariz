import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, beforeEach } from "vitest";
import { vi } from "vitest";

vi.mock("./RecordingsPanel", () => ({ default: () => <div>LIST</div> }));
vi.mock("./ChatPanel", () => ({ default: () => <div>CHAT</div> }));

// The left-panel header is now the RoomSwitcher, which reads the current room and the signed-in user's avatar.
const room = { id: "p1", name: "Personal", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63 };
vi.mock("../lib/rooms", () => ({ useRoom: () => ({ rooms: [room], currentRoom: room }) }));
vi.mock("../auth", () => ({
  useAuth: () => ({ initials: "AL", pictureUrl: null, permissions: { manageRooms: false, manageUsers: false, managePlatform: false } }),
}));

import Workspace from "./Workspace";

function renderWorkspace(initial = "/") {
  return render(
    <MemoryRouter initialEntries={[initial]}>
      <Routes>
        <Route path="/" element={<Workspace />}>
          <Route index element={<div>EMPTY</div>} />
          <Route path="recordings/:id" element={<div>DETAIL</div>} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

describe("Workspace", () => {
  beforeEach(() => localStorage.clear());

  it("shows the list by default and the chat panel starts collapsed (but stays mounted)", () => {
    renderWorkspace();
    expect(screen.getByText("LIST")).toBeTruthy();
    expect(screen.getByRole("button", { name: /expand chat panel/i })).toBeTruthy();
    // Mounted for state preservation, but inside the hidden container while collapsed.
    expect(screen.getByText("CHAT").closest(".hidden")).toBeTruthy();
  });

  it("keeps the chat panel mounted across collapse/expand (preserves its state)", () => {
    renderWorkspace();
    // Expand → not hidden; collapse → hidden; the element is never removed from the DOM.
    fireEvent.click(screen.getByRole("button", { name: /expand chat panel/i }));
    expect(screen.getByText("CHAT").closest(".hidden")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /collapse chat panel/i }));
    expect(screen.getByText("CHAT")).toBeTruthy();
    expect(screen.getByText("CHAT").closest(".hidden")).toBeTruthy();
  });

  it("collapses the left panel and persists the choice", () => {
    renderWorkspace();
    fireEvent.click(screen.getByRole("button", { name: /collapse personal panel/i }));
    expect(screen.queryByText("LIST")).toBeNull();
    expect(screen.getByRole("button", { name: /expand personal panel/i })).toBeTruthy();
    expect(localStorage.getItem("diariz.panels.left")).toBe("false");
  });

  it("expands the chat panel when requested", () => {
    renderWorkspace();
    fireEvent.click(screen.getByRole("button", { name: /expand chat panel/i }));
    expect(screen.getByText("CHAT").closest(".hidden")).toBeNull();
  });

  it("renders the routed detail in the middle panel", () => {
    renderWorkspace("/recordings/rec-1");
    expect(screen.getByText("DETAIL")).toBeTruthy();
  });

  it("drag-resizes the right panel and persists the width", () => {
    renderWorkspace();
    fireEvent.click(screen.getByRole("button", { name: /expand chat panel/i }));

    const handle = screen.getByRole("separator", { name: /resize chat panel/i });
    fireEvent.mouseDown(handle);
    fireEvent.mouseMove(document, { clientX: 700 });
    fireEvent.mouseUp(document);

    // jsdom window.innerWidth is 1024, so width = 1024 - 700 = 324 (within clamp).
    expect(localStorage.getItem("diariz.panels.rightWidth")).toBe("324");
  });
});
