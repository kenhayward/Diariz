import { createContext, useContext, useMemo, useState, type ReactNode } from "react";

/// The move clipboard: cut here, drill somewhere else, paste there. The left nav shows one folder level
/// at a time, so source and destination are never on screen together - this context is what lets a cut
/// survive the trip. It holds either a set of recordings or a single folder, never both; a new cut
/// replaces whatever was already held (folders are cut one at a time - there is no folder multi-select).
/// Session-scoped and in-memory only: nothing here is persisted, so a reload clears it, and nothing
/// touches the server until paste (a later concern, not this context's). Defaults to an empty, no-op
/// clipboard so a component used outside the provider still renders.
export type MoveClipboardKind = "recordings" | "folders";

export interface MoveClipboardCut {
  kind: MoveClipboardKind;
  ids: string[];
  /// The folder the cut items came from: the drill level open at cut time for a recordings cut, or the
  /// cut folder's own parent for a folder cut. Lets a later paste check detect "pasting back into the
  /// folder you cut from" and disable it. Null means the root (Ungrouped).
  sourceSectionId: string | null;
  /// The room the cut happened in. The clipboard survives a room switch, so a later paste check needs
  /// this to detect (and disable) pasting into a shared room.
  sourceRoomId: string | null;
}

interface MoveClipboard {
  cut: MoveClipboardCut | null;
  cutRecordings: (ids: string[], sourceSectionId: string | null, sourceRoomId: string | null) => void;
  cutFolder: (id: string, sourceSectionId: string | null, sourceRoomId: string | null) => void;
  clear: () => void;
}

const noop = () => {};
const MoveClipboardContext = createContext<MoveClipboard>({
  cut: null,
  cutRecordings: noop,
  cutFolder: noop,
  clear: noop,
});

export function MoveClipboardProvider({ children }: { children: ReactNode }) {
  const [cut, setCut] = useState<MoveClipboardCut | null>(null);

  const value = useMemo<MoveClipboard>(
    () => ({
      cut,
      cutRecordings: (ids, sourceSectionId, sourceRoomId) =>
        setCut({ kind: "recordings", ids, sourceSectionId, sourceRoomId }),
      cutFolder: (id, sourceSectionId, sourceRoomId) =>
        setCut({ kind: "folders", ids: [id], sourceSectionId, sourceRoomId }),
      clear: () => setCut(null),
    }),
    [cut],
  );

  return <MoveClipboardContext.Provider value={value}>{children}</MoveClipboardContext.Provider>;
}

export function useMoveClipboard(): MoveClipboard {
  return useContext(MoveClipboardContext);
}
