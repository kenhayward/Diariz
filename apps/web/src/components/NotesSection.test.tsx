import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import NotesSection from "./NotesSection";
import type { MeetingNote } from "../lib/types";

const note = (over: Partial<MeetingNote> = {}): MeetingNote => ({
  id: "n1", text: "Comp expectations", capturedAtMs: 61_000, ordinal: 0,
  createdAt: new Date("2026-07-07T10:00:00Z").toISOString(), ...over,
});

describe("NotesSection", () => {
  it("lists lines with mm:ss stamps; clicking a stamp jumps", () => {
    const onJump = vi.fn();
    render(<NotesSection notes={[note()]} onAdd={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} onJump={onJump} />);
    fireEvent.click(screen.getByRole("button", { name: /jump to 1:01/i }));
    expect(onJump).toHaveBeenCalledWith(61_000);
  });

  it("rolls stamps over into h:mm:ss past one hour", () => {
    render(
      <NotesSection
        notes={[note({ capturedAtMs: 3_904_000 })]} // 1h 05m 04s
        onAdd={vi.fn()}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
      />,
    );
    expect(screen.getByText("1:05:04")).toBeTruthy();
  });

  it("shows no stamp for unstamped lines", () => {
    render(<NotesSection notes={[note({ capturedAtMs: null })]} onAdd={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} />);
    expect(screen.queryByRole("button", { name: /jump to/i })).toBeNull();
  });

  it("adds a line on Enter and clears the box", () => {
    const onAdd = vi.fn();
    render(<NotesSection notes={[]} onAdd={onAdd} onEdit={vi.fn()} onDelete={vi.fn()} />);
    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "IPO experience APAC" } });
    fireEvent.keyDown(box, { key: "Enter" });
    expect(onAdd).toHaveBeenCalledWith("IPO experience APAC");
    expect((box as HTMLInputElement).value).toBe("");
  });

  it("edits and deletes via the row controls", () => {
    const onEdit = vi.fn();
    const onDelete = vi.fn();
    render(<NotesSection notes={[note()]} onAdd={vi.fn()} onEdit={onEdit} onDelete={onDelete} />);
    fireEvent.click(screen.getByRole("button", { name: /edit note/i }));
    const input = screen.getByDisplayValue("Comp expectations");
    fireEvent.change(input, { target: { value: "Comp + equity" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    expect(onEdit).toHaveBeenCalledWith("n1", "Comp + equity");
    fireEvent.click(screen.getByRole("button", { name: /delete note/i }));
    expect(onDelete).toHaveBeenCalledWith("n1");
  });

  it("shows the empty hint when there are no notes", () => {
    render(<NotesSection notes={[]} onAdd={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} />);
    expect(screen.getByText(/no notes yet/i)).toBeTruthy();
  });

  // A room co-viewer can read a recording's notes but not add/edit/delete them - the API rejects those
  // routes for anyone but the owner, so the read-only caller must not be offered controls that would 403/404.
  it("hides the add box and the edit/delete controls when no mutation handlers are given (read-only)", () => {
    render(<NotesSection notes={[note()]} />);

    expect(screen.getByText("Comp expectations")).toBeTruthy(); // the line itself still shows
    expect(screen.queryByPlaceholderText(/add a note/i)).toBeNull();
    expect(screen.queryByRole("button", { name: /edit note/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /delete note/i })).toBeNull();
  });

  it("still offers the jump stamp when read-only (jumping isn't a mutation)", () => {
    const onJump = vi.fn();
    render(<NotesSection notes={[note()]} onJump={onJump} />);

    fireEvent.click(screen.getByRole("button", { name: /jump to 1:01/i }));
    expect(onJump).toHaveBeenCalledWith(61_000);
  });
});
