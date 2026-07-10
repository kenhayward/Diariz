import { createContext, useContext, useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { useParams } from "react-router-dom";
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
  /// The folder selected in the current room's panel (null = none / Ungrouped). A new recording snapshots this
  /// when Record is pressed, so it lands where the user was looking.
  selectedSectionId: string | null;
  setSelectedSectionId: (id: string | null) => void;
  isLoading: boolean;
}

const RoomContext = createContext<RoomState | null>(null);

export function RoomProvider({ children }: { children: ReactNode }) {
  const { roomId } = useParams();
  const [selectedSectionId, setSelectedSectionId] = useState<string | null>(null);
  const { data: rooms = [], isLoading } = useQuery({ queryKey: ["rooms"], queryFn: api.listRooms });

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
      setSelectedSectionId,
      isLoading,
    }),
    [rooms, currentRoom, permissions, selectedSectionId, isLoading],
  );

  return <RoomContext.Provider value={value}>{children}</RoomContext.Provider>;
}

export function useRoom(): RoomState {
  const ctx = useContext(RoomContext);
  if (!ctx) throw new Error("useRoom must be used within RoomProvider");
  return ctx;
}
