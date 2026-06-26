import { render, screen, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, beforeEach } from "vitest";
import { vi } from "vitest";

vi.mock("./RecordingsPanel", () => ({ default: () => <div>LIST</div> }));
vi.mock("./ChatPanel", () => ({ default: () => <div>CHAT</div> }));

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

  it("shows the list by default and the chat panel starts collapsed", () => {
    renderWorkspace();
    expect(screen.getByText("LIST")).toBeTruthy();
    expect(screen.queryByText("CHAT")).toBeNull();
    expect(screen.getByRole("button", { name: /expand chat panel/i })).toBeTruthy();
  });

  it("collapses the left panel and persists the choice", () => {
    renderWorkspace();
    fireEvent.click(screen.getByRole("button", { name: /collapse recordings panel/i }));
    expect(screen.queryByText("LIST")).toBeNull();
    expect(screen.getByRole("button", { name: /expand recordings panel/i })).toBeTruthy();
    expect(localStorage.getItem("diariz.panels.left")).toBe("false");
  });

  it("expands the chat panel when requested", () => {
    renderWorkspace();
    fireEvent.click(screen.getByRole("button", { name: /expand chat panel/i }));
    expect(screen.getByText("CHAT")).toBeTruthy();
  });

  it("renders the routed detail in the middle panel", () => {
    renderWorkspace("/recordings/rec-1");
    expect(screen.getByText("DETAIL")).toBeTruthy();
  });
});
