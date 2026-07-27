import { createContext, useCallback, useContext, useState, type ReactNode } from "react";
import HelpPopover from "../../components/HelpPopover";

interface OpenHelp {
  topic: string;
  /** Where the `?` button that asked for this help sits, in viewport coordinates. */
  anchor: DOMRect;
}

interface HelpValue {
  openTopic: string | null;
  openHelp: (topic: string, anchor: DOMRect) => void;
  closeHelp: () => void;
}

const noop = () => {};
const HelpContext = createContext<HelpValue>({ openTopic: null, openHelp: noop, closeHelp: noop });

/// Owns the single contextual-help popover.
///
/// The state lives here rather than in each `HelpButton` for two reasons. First, the panel is rendered
/// once, into `document.body` via a portal, so it escapes the stacking and overflow contexts of the
/// modals the `?` buttons sit inside - a locally rendered popover would be clipped or stacked
/// underneath. Second, it makes "only one open at a time" structural rather than something every call
/// site has to cooperate on.
///
/// Mounted in `main.tsx`, not `WorkspaceLayout`, so `?` buttons work on the standalone pages too.
export function HelpProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState<OpenHelp | null>(null);

  const openHelp = useCallback((topic: string, anchor: DOMRect) => {
    // Clicking the button whose popover is already open toggles it shut, which is what a second click
    // on a `?` is asking for.
    setOpen((cur) => (cur?.topic === topic ? null : { topic, anchor }));
  }, []);

  const closeHelp = useCallback(() => setOpen(null), []);

  return (
    <HelpContext.Provider value={{ openTopic: open?.topic ?? null, openHelp, closeHelp }}>
      {children}
      {open && <HelpPopover topic={open.topic} anchor={open.anchor} onClose={closeHelp} />}
    </HelpContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useHelp(): HelpValue {
  return useContext(HelpContext);
}
