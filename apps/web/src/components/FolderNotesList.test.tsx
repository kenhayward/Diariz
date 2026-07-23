import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { I18nextProvider } from "react-i18next";
import i18n from "../lib/i18n";

// The room the folder page is viewing; the meeting link must stay inside it.
const roomState = { currentRoom: undefined as { id: string; isPersonal: boolean } | undefined };
vi.mock("../lib/rooms", () => ({
  useRoomBasePath: () =>
    roomState.currentRoom && !roomState.currentRoom.isPersonal ? `/rooms/${roomState.currentRoom.id}` : "",
}));

import FolderNotesList from "./FolderNotesList";
import type { SectionNoteItem } from "../lib/types";

const note: SectionNoteItem = {
  id: "n1",
  recordingId: "r1",
  recordingName: "Kickoff",
  text: "Follow up with Sam",
  capturedAtMs: null,
  ordinal: 0,
  createdAt: "2026-01-01T00:00:00Z",
  recordedByUserId: "u1",
};

function renderList(items: SectionNoteItem[] = [note], myUserId: string | null = "u1") {
  return render(
    <I18nextProvider i18n={i18n}>
      <MemoryRouter>
        <FolderNotesList items={items} myUserId={myUserId} onEdit={() => {}} onDelete={() => {}} />
      </MemoryRouter>
    </I18nextProvider>,
  );
}

describe("FolderNotesList room-aware meeting link", () => {
  beforeEach(() => {
    roomState.currentRoom = undefined;
  });

  it("links to the recording within the current shared room", () => {
    roomState.currentRoom = { id: "room-s", isPersonal: false };
    renderList();
    expect(screen.getByRole("link", { name: "Kickoff" }).getAttribute("href")).toBe("/rooms/room-s/recordings/r1");
  });

  it("links at the top level in the personal room", () => {
    roomState.currentRoom = { id: "p1", isPersonal: true };
    renderList();
    expect(screen.getByRole("link", { name: "Kickoff" }).getAttribute("href")).toBe("/recordings/r1");
  });
});

describe("FolderNotesList ownership gating", () => {
  const mine: SectionNoteItem = { ...note, id: "n1", text: "Mine", recordedByUserId: "u1" };
  const theirs: SectionNoteItem = { ...note, id: "n2", text: "Theirs", recordedByUserId: "u2" };

  it("shows edit + delete for the caller's own row but not for a co-viewer's row, in the same list", () => {
    renderList([mine, theirs], "u1");

    // Row 1 (mine): editable input + delete button present.
    expect(screen.getByLabelText("Note 1")).toBeTruthy();
    expect(screen.getByLabelText("Remove note 1")).toBeTruthy();

    // Row 2 (theirs): no editable input, no delete button - the API 404s those routes for anyone
    // but the recording's owner, so the control isn't offered. The text still reads.
    expect(screen.queryByLabelText("Note 2")).toBeNull();
    expect(screen.queryByLabelText("Remove note 2")).toBeNull();
    expect(screen.getByText("Theirs")).toBeTruthy();
  });
});
