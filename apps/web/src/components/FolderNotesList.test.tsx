import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));

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
};

function renderList() {
  return render(
    <MemoryRouter>
      <FolderNotesList items={[note]} onEdit={() => {}} onDelete={() => {}} />
    </MemoryRouter>,
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
