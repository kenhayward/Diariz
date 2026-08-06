/// Which tab the left recordings panel is showing, as a tiny external store rather than component state.
///
/// The panel owns the tab strip, so `useState` was the obvious home for this - until something *outside* the
/// panel needed to move it. The recording detail page's folder chips do: clicking one means "show me this
/// folder in the list", which has to pull the panel off Calendar/Actions/Tags first. The detail page is a
/// sibling of the panel, not a child, so it cannot call a setter, and writing `localStorage` behind the
/// panel's back does nothing - a `useState` initialiser runs once.
///
/// `useSyncExternalStore` is the whole implementation: subscribers are notified on every write, and the
/// snapshot is a plain string, so returning a freshly-read value each call is stable by value (`Object.is`)
/// and cannot loop. localStorage stays the persistence layer, exactly as before.

import { useSyncExternalStore } from "react";

export type PanelTab = "list" | "calendar" | "actions" | "tags";

/// Unchanged from when the panel owned this outright, so a user's persisted tab survives the change.
export const PANEL_TAB_KEY = "diariz.recordings.tab";

const TABS: readonly PanelTab[] = ["list", "calendar", "actions", "tags"];

const listeners = new Set<() => void>();

/// Anything that isn't a tab reads as `list` - covers a first visit, a value from an older build, and a
/// hand-edited key. The panel must always have a tab to render.
function getSnapshot(): PanelTab {
  let stored: string | null = null;
  try {
    stored = localStorage.getItem(PANEL_TAB_KEY);
  } catch {
    /* storage disabled: fall through to the default */
  }
  return TABS.includes(stored as PanelTab) ? (stored as PanelTab) : "list";
}

function subscribe(onChange: () => void): () => void {
  listeners.add(onChange);
  return () => {
    listeners.delete(onChange);
  };
}

export function setPanelTab(tab: PanelTab): void {
  try {
    localStorage.setItem(PANEL_TAB_KEY, tab);
  } catch {
    /* storage disabled (private mode): the tab still moves this session, it just won't persist */
  }
  for (const notify of listeners) notify();
}

export function usePanelTab(): PanelTab {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}
