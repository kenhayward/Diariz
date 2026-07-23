import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { I18nextProvider } from "react-i18next";
import i18n from "../lib/i18n";

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
  recordedByUserId: "u1",
};

function renderList(items: SectionAttachmentItem[] = [attachment], myUserId: string | null = "u1") {
  return render(
    <I18nextProvider i18n={i18n}>
      <MemoryRouter>
        <FolderAttachmentsList items={items} myUserId={myUserId} onRemove={() => {}} />
      </MemoryRouter>
    </I18nextProvider>,
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

describe("FolderAttachmentsList ownership gating", () => {
  beforeEach(() => {
    roomState.currentRoom = undefined;
  });

  const mineFile: SectionAttachmentItem = {
    ...attachment, id: "at1", kind: "File", name: "mine.pdf", recordedByUserId: "u1",
  };
  const theirsFile: SectionAttachmentItem = {
    ...attachment, id: "at2", kind: "File", name: "theirs.pdf", recordedByUserId: "u2",
  };
  const theirsUrl: SectionAttachmentItem = {
    ...attachment, id: "at3", kind: "Url", name: "their-link", recordedByUserId: "u2",
  };

  it("shows Open + Remove for the caller's own file row but not for a co-viewer's file row", () => {
    renderList([mineFile, theirsFile], "u1");

    // Row 1 (mine): open + remove both present.
    const mineRow = screen.getByText("mine.pdf").closest("tr")!;
    expect(within(mineRow).getByText(/^open$/i)).toBeTruthy();
    expect(within(mineRow).getByTitle("Remove")).toBeTruthy();

    // Row 2 (theirs, a File kind): the content route is owner-only, so opening it would 404 -
    // neither control is offered. The row still reads.
    const theirsRow = screen.getByText("theirs.pdf").closest("tr")!;
    expect(within(theirsRow).queryByText(/^open$/i)).toBeNull();
    expect(within(theirsRow).queryByTitle("Remove")).toBeNull();
  });

  it("still allows opening a co-viewer's URL-kind row (no API call - the address is public data) but not removing it", () => {
    renderList([theirsUrl], "u1");
    const row = screen.getByText("their-link").closest("tr")!;
    expect(within(row).getByText(/^open$/i)).toBeTruthy();
    expect(within(row).queryByTitle("Remove")).toBeNull();
  });
});
