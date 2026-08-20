import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, beforeEach } from "vitest";
import { vi } from "vitest";

vi.mock("./RecordingsPanel", () => ({ default: () => <div>LIST</div> }));
vi.mock("./ChatPanel", () => ({ default: () => <div>CHAT</div> }));
vi.mock("./hub/CaptureBar", () => ({
  default: () => <div data-tour="capture">CAPTURE</div>,
}));
// The account menu is a real component with react-query + auth dependencies; this test is about the shell.
vi.mock("./UserMenu", () => ({
  default: () => <button data-tour="account" type="button">ACCOUNT</button>,
}));

// The left-panel header is now the RoomSwitcher, which reads the current room and the signed-in user's avatar.
const room = { id: "p1", name: "Personal", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63 };
vi.mock("../lib/rooms", () => ({ useRoom: () => ({ rooms: [room], currentRoom: room }) }));
vi.mock("../auth", () => ({
  useAuth: () => ({ initials: "AL", pictureUrl: null, permissions: { manageRooms: false, manageUsers: false, managePlatform: false } }),
}));

import Workspace from "./Workspace";
import { TOUR_STEPS } from "../lib/onboarding";

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

  it("puts the account menu in the left panel's room row, next to the room switcher", () => {
    renderWorkspace();
    const account = screen.getByRole("button", { name: "ACCOUNT" });
    const collapse = screen.getByRole("button", { name: /collapse personal panel/i });
    // Proves the `leading` slot lands inside RoomSwitcher's row (the same row the collapse control ends) -
    // not that the two triggers are literal siblings in the real tree. That only holds here because this
    // stub is a bare <button>; the real UserMenu (UserMenu.tsx) wraps its trigger in its own
    // `<div className="relative">`, so in production the button's actual parent is that wrapper, not the row.
    expect(account.parentElement).toBe(collapse.parentElement);
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

  // The bar spans the routed content only: it shares a column with <main>, and the chat rail is outside
  // that column so the bar never runs over it.
  it("renders the capture bar above the routed content, inside the content column", () => {
    renderWorkspace("/recordings/rec-1");
    const bar = screen.getByText("CAPTURE");
    const main = document.querySelector("main")!;
    expect(bar.parentElement).toBe(main.parentElement);
    expect(bar.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("keeps the chat rail out of the capture bar's column", () => {
    renderWorkspace();
    const column = screen.getByText("CAPTURE").parentElement!;
    const rail = screen.getByRole("button", { name: /expand chat panel/i });
    expect(column.contains(rail)).toBe(false);
  });

  // The tour spotlights each step's region by attribute, and every step's region lives in the workspace.
  // The capture cluster moved into the capture bar and the account pill into the room row; if either
  // anchor were dropped in the move, the tour would dim the app with nothing lit.
  it("renders a region for every tour step", () => {
    renderWorkspace("/recordings/rec-1");
    for (const step of TOUR_STEPS) {
      expect(document.querySelector(`[data-tour="${step.target}"]`)).toBeTruthy();
    }
  });

  // At a narrow window the capture bar's cluster is wider than the content column and spills past that
  // column's right edge (the cluster is min-w-0 so the box shrinks, but the flex row inside does not clip -
  // and it cannot, because the recorder's popovers are absolute children that overflow-hidden would cut).
  // The Recorder's root is `position: relative`, so the spill paints in the positioned-descendant phase,
  // above any plain in-flow sibling - which is what the chat rail was, so the clock and upload buttons
  // landed on top of the chat panel's header. Giving the rail its own positive layer puts the chat panel
  // over the spill instead. jsdom computes no geometry, so this pins the class contract; the layering
  // itself was measured in a browser.
  it("puts the open chat panel on a layer above the capture bar's overflow", () => {
    renderWorkspace();
    fireEvent.click(screen.getByRole("button", { name: /expand chat panel/i }));
    const panel = screen.getByText("CHAT").closest("[data-tour='chat']")!;
    expect(panel.className).toContain("relative");
    expect(panel.className).toContain("z-10");
  });

  // Same spill, same rule: collapsed, the chat is a 36px rail in the same sibling position.
  it("puts the collapsed chat rail on that layer too", () => {
    renderWorkspace();
    const rail = screen.getByRole("button", { name: /expand chat panel/i }).parentElement!;
    expect(rail.className).toContain("relative");
    expect(rail.className).toContain("z-10");
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
