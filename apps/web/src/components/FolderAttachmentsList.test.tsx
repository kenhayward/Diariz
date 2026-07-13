import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));

vi.mock("../lib/api", () => ({ api: {} }));

// The room the folder page is viewing; the meeting link must stay inside it.
const roomState = { currentRoom: undefined as { id: string; isPersonal: boolean } | undefined };
vi.mock("../lib/rooms", () => ({
  useRoomBasePath: () =>
    roomState.currentRoom && !roomState.currentRoom.isPersonal ? `/rooms/${roomState.currentRoom.id}` : "",
}));

import FolderAttachmentsList from "./FolderAttachmentsList";
import type { SectionAttachmentItem } from "../lib/types";

const attachment: SectionAttachmentItem = {
  id: "at1",
  recordingId: "r1",
  recordingName: "Kickoff",
  kind: "Url",
  name: "Agenda link",
  contentType: null,
  sizeBytes: 0,
  url: "https://example.com/agenda",
  ordinal: 0,
};

function renderList() {
  return render(
    <MemoryRouter>
      <FolderAttachmentsList items={[attachment]} onRemove={() => {}} />
    </MemoryRouter>,
  );
}

describe("FolderAttachmentsList room-aware meeting link", () => {
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
