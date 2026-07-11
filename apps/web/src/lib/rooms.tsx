import { createContext, useContext, useMemo, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { useMatch } from "react-router-dom";
import { api } from "./api";
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
  selectedSectionId: string | null;
  /// Where a new recording should be filed, resolving the user's placement preference against the folder they
  /// are viewing: Ungrouped -> null, SpecificFolder -> the configured folder, SelectedFolder -> selectedSectionId.
  /// The Recorder snapshots this when Record is pressed.
  recordingSectionId: string | null;
  isLoading: boolean;
}

const RoomContext = createContext<RoomState | null>(null);

export function RoomProvider({ children }: { children: ReactNode }) {
  // useMatch (not useParams) so this works even though RoomProvider sits on the parent "/" route, above the
  // route that carries :roomId. Matches /rooms/:roomId and any nested detail under it.
  const roomId = useMatch("/rooms/:roomId/*")?.params.roomId;
  // The folder currently open, from either the legacy or the room-scoped section route.
  const selectedSectionId =
    useMatch("/sections/:id")?.params.id ?? useMatch("/rooms/:roomId/sections/:id")?.params.id ?? null;
  const { data: rooms = [], isLoading } = useQuery({ queryKey: ["rooms"], queryFn: api.listRooms });
  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  // Resolve the placement preference. Defaults to "the folder you're viewing" until settings load.
  const placementMode = settings?.placementMode ?? "SelectedFolder";
  const recordingSectionId =
    placementMode === "Ungrouped"
      ? null
      : placementMode === "SpecificFolder"
        ? settings?.placementSectionId ?? null
        : selectedSectionId;

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
