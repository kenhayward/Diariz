import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import LiveNotesPanel from "./LiveNotesPanel";

const lines = [{ id: "l1", text: "Comp expectations", capturedAtMs: 61_000, ordinal: 0, createdAt: "" }];

describe("LiveNotesPanel", () => {
  it("renders committed lines with stamps and commits a new one on Enter", () => {
    const onAdd = vi.fn();
    render(<LiveNotesPanel lines={lines} onAdd={onAdd} onEdit={vi.fn()} onDelete={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByText("Comp expectations")).toBeTruthy();
    expect(screen.getByText("1:01")).toBeTruthy();
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "IPO experience" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(onAdd).toHaveBeenCalledWith("IPO experience");
  });

  it("closes via the header button", () => {
    const onClose = vi.fn();
    render(<LiveNotesPanel lines={[]} onAdd={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} onClose={onClose} />);
    fireEvent.click(screen.getByRole("button", { name: /close notes/i }));
    expect(onClose).toHaveBeenCalled();
  });
});
