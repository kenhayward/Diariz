import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi } from "vitest";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));

// The Actions panel selects rows via the shared selection context; a no-op stub is enough here.
vi.mock("../lib/selection", () => ({
  useSelection: () => ({ selectMode: false, selectedIds: [] as string[], toggle: () => {}, set: () => {} }),
}));

// The room the Actions panel is viewing; its links must stay inside it.
const roomState = { currentRoom: undefined as { id: string; isPersonal: boolean } | undefined };
vi.mock("../lib/rooms", () => ({
  useRoomBasePath: () =>
    roomState.currentRoom && !roomState.currentRoom.isPersonal ? `/rooms/${roomState.currentRoom.id}` : "",
}));

import ActionsTab from "./ActionsTab";
import type { ActionListItem } from "../lib/types";

const action: ActionListItem = {
  id: "a1",
  recordingId: "r1",
  recordingName: "Kickoff",
  text: "Send the deck",
  actor: "Sam",
  deadline: "",
  ordinal: 0,
  completed: false,
  completedAt: null,
  createdAt: "2026-01-01T00:00:00Z",
};

function renderTab() {
  return render(
    <MemoryRouter>
      <ActionsTab actions={[action]} persons={["Sam"]} person={null} onPerson={() => {}} />
    </MemoryRouter>,
  );
}

describe("ActionsTab room-aware links", () => {
  it("links an action to its recording within the current shared room", () => {
    roomState.currentRoom = { id: "room-s", isPersonal: false };
    renderTab();
    const link = screen.getByRole("link", { name: "Send the deck" });
    expect(link.getAttribute("href")).toBe("/rooms/room-s/recordings/r1");
  });

  it("links at the top level in the personal room", () => {
    roomState.currentRoom = { id: "p1", isPersonal: true };
    renderTab();
    const link = screen.getByRole("link", { name: "Send the deck" });
    expect(link.getAttribute("href")).toBe("/recordings/r1");
  });
});
