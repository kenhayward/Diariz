import { createContext, useContext, useMemo, useState } from "react";

/// Recording selection shared between the left list (where you pick) and the chat panel (where the
/// selection becomes the "Selected transcript" context). Defaults to an empty, no-op selection so a
/// component used outside the provider still renders.
interface Selection {
  selectMode: boolean;
  setSelectMode: (v: boolean) => void;
  selectedIds: string[];
  toggle: (id: string) => void;
  set: (ids: string[]) => void;
  clear: () => void;
}

const noop = () => {};
const SelectionContext = createContext<Selection>({
  selectMode: false,
  setSelectMode: noop,
  selectedIds: [],
  toggle: noop,
  set: noop,
  clear: noop,
});

export function SelectionProvider({ children }: { children: React.ReactNode }) {
  const [selectMode, setSelectMode] = useState(false);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);

  const value = useMemo<Selection>(
    () => ({
      selectMode,
      setSelectMode,
      selectedIds,
      toggle: (id) => setSelectedIds((p) => (p.includes(id) ? p.filter((x) => x !== id) : [...p, id])),
      set: (ids) => setSelectedIds(ids),
      clear: () => setSelectedIds([]),
    }),
    [selectMode, selectedIds],
  );

  return <SelectionContext.Provider value={value}>{children}</SelectionContext.Provider>;
}

export function useSelection(): Selection {
  return useContext(SelectionContext);
}
