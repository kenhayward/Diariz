/// Remembering which room you were last in, so returning to the app puts you back rather than always in your
/// personal room.
///
/// The **URL is still the authority** (`/rooms/:roomId`); this is only consulted on a fresh landing at `/`,
/// where there is nothing else to go on. Write on change, read once on landing - never read every render, or
/// it starts fighting the URL.

import type { RoomListItem } from "./types";

export const ROOM_KEY = "diariz.rooms.currentRoomId";

export function rememberRoom(roomId: string): void {
  localStorage.setItem(ROOM_KEY, roomId);
}

/// Where a bare landing at `/` should redirect to, or null to stay put.
///
/// Null for the personal room even when it is the remembered one: its detail routes *are* the top-level
/// routes (`useRoomBasePath` returns ""), so `/rooms/<personal-id>` would be a second, redundant URL for the
/// same place. Null too for a room that has since been left or deleted - land somewhere real, not on a 404 -
/// and while `rooms` is still loading, since "not in the list" and "no list yet" are indistinguishable here.
export function landingRoomPath(rooms: RoomListItem[]): string | null {
  if (rooms.length === 0) return null;
  const remembered = localStorage.getItem(ROOM_KEY);
  if (!remembered) return null;

  const room = rooms.find((r) => r.id === remembered);
  if (!room || room.isPersonal) return null;
  return `/rooms/${room.id}`;
}
