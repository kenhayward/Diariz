import { createContext, useContext, useEffect, useMemo, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { useMatch, useNavigate, useLocation } from "react-router-dom";
import { api } from "./api";
import { useDrillSectionId } from "./drillRoute";
import { rememberRoom, landingRoomPath } from "./roomPersistence";
import type { RoomListItem } from "./types";

/// The room the workspace is currently showing, plus the caller's authority inside it and the folder they have
/// selected. One room exists today (each user's Personal room); the switcher and `/rooms/:roomId` routing make
/// this meaningful once shared rooms arrive. Permissions are a RoomPermission bitmask - 0 (and every `can`
/// false) while the list is loading, so the UI fails closed.
interface RoomState {
  rooms: RoomListItem[];
  currentRoom: RoomListItem | undefined;
  permissions: number;
  can: (perm: number) => boolean;
  /// The folder the user is currently viewing (a /sections/:id page), or null when not on a folder page.
  /// This is the folder open in the **middle panel**, which is NOT where the recordings list is browsing -
  /// see `recordingSectionId` for the folder a new recording belongs to.
  selectedSectionId: string | null;
  /// Where a new recording should be filed, resolving the user's placement preference against the folder they
  /// are working in: Ungrouped -> null, SpecificFolder -> the configured folder, SelectedFolder -> the folder
  /// the recordings list is drilled into (`?in=`), falling back to an open folder page when it is at the root.
  /// The Recorder snapshots this when Record is pressed.
  recordingSectionId: string | null;
  isLoading: boolean;
}

const RoomContext = createContext<RoomState | null>(null);

export function RoomProvider({ children }: { children: ReactNode }) {
  // useMatch (not useParams) so this works even though RoomProvider sits on the parent "/" route, above the
  // route that carries :roomId. Matches /rooms/:roomId and any nested detail under it.
  const roomId = useMatch("/rooms/:roomId/*")?.params.roomId;
  // The folder currently open, from either the legacy or the room-scoped section route. Both useMatch calls
  // must be UNCONDITIONAL: joining them with `??` short-circuits (skips) the second hook whenever the first
  // (/sections/:id) matches, so the hook count drops between renders when you open a personal folder - a
  // Rules-of-Hooks violation that crashed React with "reading 'length'" (issue #289). Combine the results,
  // not the hook calls.
  const legacySectionMatch = useMatch("/sections/:id");
  const roomSectionMatch = useMatch("/rooms/:roomId/sections/:id");
  const selectedSectionId = legacySectionMatch?.params.id ?? roomSectionMatch?.params.id ?? null;
  const { data: rooms = [], isLoading } = useQuery({ queryKey: ["rooms"], queryFn: api.listRooms });
  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  // Where the recordings list is browsing. This - not `selectedSectionId` - is the folder a user means by
  // "the folder I'm in": clicking a folder row drills the list (`?in=`) and deliberately does NOT open
  // /sections/:id (see drillRoute.ts), so resolving SelectedFolder from the route match alone filed every
  // take into Ungrouped unless the user had also opened the folder's own page from the breadcrumb menu.
  const drilledSectionId = useDrillSectionId().sectionId;

  // Resolve the placement preference. Defaults to "the folder you're viewing" until settings load. The open
  // folder page is the fallback for SelectedFolder, so a deep link straight to /sections/:id (which leaves
  // the list at the room root) still counts as being in that folder.
  const placementMode = settings?.placementMode ?? "SelectedFolder";
  const recordingSectionId =
    placementMode === "Ungrouped"
      ? null
      : placementMode === "SpecificFolder"
        ? settings?.placementSectionId ?? null
        : drilledSectionId ?? selectedSectionId;

  // Prefer the room named in the URL; else the personal room; else whatever came first.
  const currentRoom = useMemo(() => {
    if (rooms.length === 0) return undefined;
    return (
      (roomId ? rooms.find((r) => r.id === roomId) : undefined) ??
      rooms.find((r) => r.isPersonal) ??
      rooms[0]
    );
  }, [rooms, roomId]);

  const permissions = currentRoom?.permissions ?? 0;

  // Remember where you were, and put you back there on a bare landing at "/". The URL still wins whenever it
  // names a room - this only fills the gap when it doesn't. `replace` so the redirect isn't a history entry
  // the back button has to fight through.
  const navigate = useNavigate();
  const { pathname } = useLocation();
  useEffect(() => {
    if (currentRoom) rememberRoom(currentRoom.id);
  }, [currentRoom]);
  useEffect(() => {
    if (pathname !== "/") return; // only a bare landing; any real route already says where to be
    const target = landingRoomPath(rooms);
    if (target) navigate(target, { replace: true });
  }, [pathname, rooms, navigate]);

  const value = useMemo<RoomState>(
    () => ({
      rooms,
      currentRoom,
      permissions,
      can: (perm) => (permissions & perm) !== 0,
      selectedSectionId,
      recordingSectionId,
      isLoading,
    }),
    [rooms, currentRoom, permissions, selectedSectionId, recordingSectionId, isLoading],
  );

  return <RoomContext.Provider value={value}>{children}</RoomContext.Provider>;
}

export function useRoom(): RoomState {
  const ctx = useContext(RoomContext);
  if (!ctx) throw new Error("useRoom must be used within RoomProvider");
  return ctx;
}

/// The URL prefix that keeps navigation inside the current room: "/rooms/:id" for a shared room, "" for the
/// personal room (its detail routes are the top-level ones). Links must use this so opening a recording in a
/// shared room stays in that room instead of falling back to the personal room (whose URL has no :roomId).
/// Reads the context directly (not via useRoom) so a row rendered outside a RoomProvider - e.g. in an isolated
/// test - falls back to the top-level route rather than throwing.
export function useRoomBasePath(): string {
  const room = useContext(RoomContext)?.currentRoom;
  return room && !room.isPersonal ? `/rooms/${room.id}` : "";
}

/// The current room's id when it is a shared room, else undefined (the personal room is the server default).
/// For scoping section/placement writes to the room being viewed. Reads the context directly so it is safe
/// outside a RoomProvider (returns undefined).
export function useSharedRoomId(): string | undefined {
  const room = useContext(RoomContext)?.currentRoom;
  return room && !room.isPersonal ? room.id : undefined;
}
