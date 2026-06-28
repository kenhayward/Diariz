import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import ActionsPanel from "./ActionsPanel";
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
  const handlers = { onAdd: vi.fn(), onUpdate: vi.fn(), onDelete: vi.fn() };
  render(<ActionsPanel actions={actions} {...handlers} />);
  return handlers;
}

describe("ActionsPanel", () => {
  it("shows the Actions heading and the column headers", () => {
    build([action()]);
    expect(screen.getByRole("heading", { name: /actions/i })).toBeTruthy();
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

  it("collapses and expands the panel via its toggle", () => {
    build([action()]);
    expect(screen.getByLabelText("Action 1")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /collapse actions panel/i }));
    expect(screen.queryByLabelText("Action 1")).toBeNull();
    expect(screen.queryByRole("button", { name: /add action/i })).toBeNull();
    // The heading stays visible while collapsed.
    expect(screen.getByRole("heading", { name: /actions/i })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /expand actions panel/i }));
    expect(screen.getByLabelText("Action 1")).toBeTruthy();
  });

  it("shows an empty state but still offers Add when there are no actions", () => {
    const h = build([]);
    expect(screen.getByText(/no actions identified/i)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /add action/i }));
    expect(h.onAdd).toHaveBeenCalled();
  });
});
