import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

/** The command-hub popovers that share the "one open at a time" state. */
export type HubPopoverId = "source" | "stop" | "notes" | "acct";

export type HubPopoverApi = {
  /** The currently-open popover, or null when all are closed. */
  openId: HubPopoverId | null;
  /** Open `id`, closing any other; toggling the already-open one closes it. */
  toggle: (id: HubPopoverId) => void;
  /** Close whatever popover is open. */
  close: () => void;
  /** Whether `id` is the open popover. */
  isOpen: (id: HubPopoverId) => boolean;
};

const HubPopoverContext = createContext<HubPopoverApi | null>(null);

// Shared single-open-popover state. A trigger calls `toggle(id)`; the hub renders each `HubPopover` with
// `open={isOpen(id)}` and `onClose={close}`. Opening one popover closes the others.
function useHubPopoverState(): HubPopoverApi {
  const [openId, setOpenId] = useState<HubPopoverId | null>(null);
  const toggle = useCallback((id: HubPopoverId) => setOpenId((cur) => (cur === id ? null : id)), []);
  const close = useCallback(() => setOpenId(null), []);
  const isOpen = useCallback((id: HubPopoverId) => openId === id, [openId]);
  return useMemo(() => ({ openId, toggle, close, isOpen }), [openId, toggle, close, isOpen]);
}

/**
 * Provides the shared "one popover open at a time" state to the top-bar cluster + avatar. Wrap the region
 * that hosts the audio-source, auto-stop, notes and account popovers so opening any one closes the rest.
 */
export function HubPopoverProvider({ children }: { children: ReactNode }) {
  const api = useHubPopoverState();
  return <HubPopoverContext.Provider value={api}>{children}</HubPopoverContext.Provider>;
}

/**
 * Access the shared popover state. When called outside a `HubPopoverProvider` it transparently falls back
 * to a self-contained local state, so a component (e.g. Recorder) still works when rendered in isolation
 * (as it is in unit tests) without needing the provider around it. Inside a provider the fallback is
 * inert - the provider's value wins.
 */
export function useHubPopover(): HubPopoverApi {
  const ctx = useContext(HubPopoverContext);
  const fallback = useHubPopoverState();
  return ctx ?? fallback;
}
