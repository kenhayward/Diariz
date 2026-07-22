import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { I18nextProvider } from "react-i18next";
import i18n from "../lib/i18n";
import FolderActionsTable from "./FolderActionsTable";
import type { ActionListItem } from "../lib/types";

// The room the folder page is viewing; its meeting links must stay inside it. Default (undefined) is the
// personal room, so the pre-existing assertions below keep expecting the top-level "/recordings/..." URL.
const roomState = { currentRoom: undefined as { id: string; isPersonal: boolean } | undefined };
vi.mock("../lib/rooms", () => ({
  useRoomBasePath: () =>
    roomState.currentRoom && !roomState.currentRoom.isPersonal ? `/rooms/${roomState.currentRoom.id}` : "",
}));

const item: ActionListItem = {
  id: "a1", recordingId: "r1", recordingName: "Kickoff", text: "Ship it", actor: "Ana",
  deadline: "Fri", ordinal: 0, completed: false, completedAt: null, createdAt: "2026-07-01T00:00:00Z",
  recordedByUserId: "u1",
};

function renderTable(props: Partial<React.ComponentProps<typeof FolderActionsTable>> = {}) {
  return render(
    <I18nextProvider i18n={i18n}>
      <MemoryRouter>
        <FolderActionsTable
          items={[item]}
          myUserId="u1"
          onUpdate={vi.fn()}
          onToggleComplete={vi.fn()}
          onDelete={vi.fn()}
          {...props}
        />
      </MemoryRouter>
    </I18nextProvider>,
  );
}

describe("FolderActionsTable", () => {
  beforeEach(() => {
    roomState.currentRoom = undefined;
  });

  it("shows the read-only Meeting column and has no add control", () => {
    renderTable();
    expect(screen.getByText("Meeting")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Kickoff" }).getAttribute("href")).toBe("/recordings/r1");
    expect(screen.queryByRole("button", { name: /add action/i })).toBeNull();
  });

  it("keeps the room prefix on the meeting link in a shared room", () => {
    roomState.currentRoom = { id: "room-s", isPersonal: false };
    renderTable();
    expect(screen.getByRole("link", { name: "Kickoff" }).getAttribute("href")).toBe("/rooms/room-s/recordings/r1");
  });

  it("commits an edit with the source recordingId", () => {
    const onUpdate = vi.fn();
    renderTable({ onUpdate });
    const cell = screen.getByLabelText("Action 1") as HTMLInputElement;
    fireEvent.change(cell, { target: { value: "Ship it now" } });
    fireEvent.blur(cell);
    expect(onUpdate).toHaveBeenCalledWith("r1", "a1", { text: "Ship it now" });
  });

  it("deletes with the source recordingId", () => {
    const onDelete = vi.fn();
    renderTable({ onDelete });
    fireEvent.click(screen.getByLabelText("Remove action 1"));
    expect(onDelete).toHaveBeenCalledWith("r1", "a1");
  });
});

describe("FolderActionsTable ownership gating", () => {
  beforeEach(() => {
    roomState.currentRoom = undefined;
  });

  const mine: ActionListItem = { ...item, id: "a1", text: "Mine", recordedByUserId: "u1" };
  const theirs: ActionListItem = { ...item, id: "a2", text: "Theirs", recordedByUserId: "u2" };

  it("shows edit/complete/delete for the caller's own row but not for a co-viewer's row, in the same list", () => {
    renderTable({ items: [mine, theirs] });

    // Row 1 (mine): editable cells, an enabled complete checkbox, and a delete button.
    expect(screen.getByLabelText("Action 1")).toBeTruthy();
    expect((screen.getByLabelText("Mark action 1 complete") as HTMLInputElement).disabled).toBe(false);
    expect(screen.getByLabelText("Remove action 1")).toBeTruthy();

    // Row 2 (theirs): no editable cell, no delete button - the API 404s (edit/delete) or silently
    // ignores (complete) those routes for anyone but the recording's owner. The checkbox stays but is
    // disabled so it doesn't look interactive; the text still reads.
    expect(screen.queryByLabelText("Action 2")).toBeNull();
    expect(screen.queryByLabelText("Remove action 2")).toBeNull();
    expect((screen.getByLabelText("Mark action 2 complete") as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText("Theirs")).toBeTruthy();
  });

  it("does not call onToggleComplete for a co-viewer's row", () => {
    const onToggleComplete = vi.fn();
    renderTable({ items: [mine, theirs], onToggleComplete });
    fireEvent.click(screen.getByLabelText("Mark action 2 complete"));
    expect(onToggleComplete).not.toHaveBeenCalled();
  });
});
