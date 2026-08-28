import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
  recordedByUserId: "me",
  // Every row in this tab is pinned by definition - that is what puts it here.
  pinned: true,
};

function renderTab(over: Partial<ActionListItem> = {}, myUserId: string | null = "me") {
  const onTogglePin = vi.fn();
  render(
    <MemoryRouter>
      <ActionsTab
        actions={[{ ...action, ...over }]}
        persons={["Sam"]}
        person={null}
        onPerson={() => {}}
        myUserId={myUserId}
        onTogglePin={onTogglePin}
      />
    </MemoryRouter>,
  );
  return onTogglePin;
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

// This file stubs i18next as `t: (k) => k`, so accessible names are translation keys, not English.
describe("ActionsTab pinning", () => {
  it("offers an unpin control on my own action", () => {
    renderTab();
    expect(screen.getByLabelText("unpinActionAria")).toBeTruthy();
  });

  it("unpins through onTogglePin", async () => {
    const onTogglePin = renderTab();
    await userEvent.click(screen.getByLabelText("unpinActionAria"));
    expect(onTogglePin).toHaveBeenCalledWith("a1", false);
  });

  it("disables the control on someone else's action, because pinning is owner-only", async () => {
    // In a shared room this tab lists other people's recordings. The API silently ignores a pin on an
    // action you do not own, so a live control here would be a button that does nothing.
    // userEvent, not fireEvent: fireEvent.click fires handlers on disabled elements, which would make this
    // pass for a reason the browser never reproduces.
    const onTogglePin = renderTab({ recordedByUserId: "someone-else" });
    const control = screen.getByLabelText("unpinActionAria") as HTMLButtonElement;
    expect(control.disabled).toBe(true);
    await userEvent.click(control);
    expect(onTogglePin).not.toHaveBeenCalled();
  });
});
