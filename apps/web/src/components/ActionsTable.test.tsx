import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ActionsTable from "./ActionsTable";
import type { RecordingAction } from "../lib/types";

const action = (over: Partial<RecordingAction> = {}): RecordingAction => ({
  id: "a1",
  text: "Send the report",
  actor: "Bob",
  deadline: "Friday",
  ordinal: 0,
  ...over,
});

function build(actions: RecordingAction[]) {
  const handlers = { onAdd: vi.fn(), onUpdate: vi.fn(), onDelete: vi.fn(), onToggleComplete: vi.fn() };
  render(<ActionsTable actions={actions} {...handlers} />);
  return handlers;
}

describe("ActionsTable", () => {
  it("shows the column headers", () => {
    build([action()]);
    expect(screen.getByText("Action")).toBeTruthy();
    expect(screen.getByText("Actor")).toBeTruthy();
    expect(screen.getByText("Deadline")).toBeTruthy();
  });

  it("renders an editable row per action with its values", () => {
    build([action()]);
    expect((screen.getByLabelText("Action 1") as HTMLInputElement).value).toBe("Send the report");
    expect((screen.getByLabelText("Actor 1") as HTMLInputElement).value).toBe("Bob");
    expect((screen.getByLabelText("Deadline 1") as HTMLInputElement).value).toBe("Friday");
  });

  it("commits an edited cell on blur via onUpdate (only the changed field)", () => {
    const h = build([action()]);
    const cell = screen.getByLabelText("Action 1");
    fireEvent.change(cell, { target: { value: "Send the deck" } });
    fireEvent.blur(cell);
    expect(h.onUpdate).toHaveBeenCalledWith("a1", { text: "Send the deck" });
  });

  it("does not call onUpdate when a cell is blurred unchanged", () => {
    const h = build([action()]);
    fireEvent.blur(screen.getByLabelText("Actor 1"));
    expect(h.onUpdate).not.toHaveBeenCalled();
  });

  it("toggles completion via the Done checkbox", () => {
    const h = build([action()]);
    fireEvent.click(screen.getByLabelText(/mark action 1 complete/i));
    expect(h.onToggleComplete).toHaveBeenCalledWith("a1", true);
  });

  it("deletes a row via its remove button", () => {
    const h = build([action()]);
    fireEvent.click(screen.getByRole("button", { name: /remove action 1/i }));
    expect(h.onDelete).toHaveBeenCalledWith("a1");
  });

  it("adds a new action via the add button", () => {
    const h = build([action()]);
    fireEvent.click(screen.getByRole("button", { name: /add action/i }));
    expect(h.onAdd).toHaveBeenCalledTimes(1);
  });

  it("shows an empty state but still offers Add when there are no actions", () => {
    const h = build([]);
    expect(screen.getByText(/no actions identified/i)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /add action/i }));
    expect(h.onAdd).toHaveBeenCalled();
  });
});
