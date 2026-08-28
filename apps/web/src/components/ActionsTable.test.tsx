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
  pinned: false,
  ...over,
});

function build(actions: RecordingAction[]) {
  const handlers = {
    onAdd: vi.fn(), onUpdate: vi.fn(), onDelete: vi.fn(), onToggleComplete: vi.fn(), onTogglePin: vi.fn(),
  };
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

  it("shows an unpinned action's pin control as the pin affordance", () => {
    build([action()]);
    expect(screen.getByLabelText("Pin action 1")).toBeTruthy();
  });

  it("shows a pinned action's control as the unpin affordance", () => {
    build([action({ pinned: true })]);
    expect(screen.getByLabelText("Unpin action 1")).toBeTruthy();
  });

  it("pins an unpinned action through onTogglePin", () => {
    const h = build([action()]);
    fireEvent.click(screen.getByLabelText("Pin action 1"));
    expect(h.onTogglePin).toHaveBeenCalledWith("a1", true);
  });

  it("unpins a pinned action through onTogglePin", () => {
    const h = build([action({ pinned: true })]);
    fireEvent.click(screen.getByLabelText("Unpin action 1"));
    expect(h.onTogglePin).toHaveBeenCalledWith("a1", false);
  });

  it("still lists unpinned actions - the recording page shows everything", () => {
    // The whole design rests on this: pinning changes the cross-meeting views, never this one. If this
    // fails, the filter has leaked down to the recording page and actions have gone missing at source.
    build([
      action({ id: "a1", text: "Book the room", pinned: true }),
      action({ id: "a2", text: "Chase the invoice" }),
    ]);
    expect((screen.getByLabelText("Action 1") as HTMLInputElement).value).toBe("Book the room");
    expect((screen.getByLabelText("Action 2") as HTMLInputElement).value).toBe("Chase the invoice");
  });

  it("shows an empty state but still offers Add when there are no actions", () => {
    const h = build([]);
    expect(screen.getByText(/no actions identified/i)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /add action/i }));
    expect(h.onAdd).toHaveBeenCalled();
  });
});
