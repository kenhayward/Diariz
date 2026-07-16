import { describe, it, expect, beforeEach } from "vitest";
import { ROOM_KEY, rememberRoom, landingRoomPath } from "./roomPersistence";

describe("roomPersistence", () => {
  beforeEach(() => localStorage.clear());

  it("remembers the room last browsed", () => {
    rememberRoom("r1");
    expect(localStorage.getItem(ROOM_KEY)).toBe("r1");
  });

  const rooms = [
    { id: "p1", isPersonal: true },
    { id: "s1", isPersonal: false },
  ];

  it("sends you back to the shared room you were last in", () => {
    rememberRoom("s1");
    expect(landingRoomPath(rooms)).toBe("/rooms/s1");
  });

  // The personal room's detail routes ARE the top-level ones (useRoomBasePath returns ""), so restoring it
  // must not push /rooms/<personal-id> - that is a different, redundant URL for the same place.
  it("does not redirect for the personal room", () => {
    rememberRoom("p1");
    expect(landingRoomPath(rooms)).toBeNull();
  });

  it("does nothing when nothing was remembered", () => {
    expect(landingRoomPath(rooms)).toBeNull();
  });

  // Left the room, or it was deleted: land somewhere real rather than a 404.
  it("ignores a remembered room the user can no longer see", () => {
    rememberRoom("gone");
    expect(landingRoomPath(rooms)).toBeNull();
  });

  it("ignores a remembered room while the list is still loading", () => {
    rememberRoom("s1");
    expect(landingRoomPath([])).toBeNull();
  });
});
