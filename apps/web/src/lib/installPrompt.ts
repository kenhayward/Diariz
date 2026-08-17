import { useEffect, useState } from "react";
import { isElectron } from "./audioSource";

/**
 * Chromium's install flow, wrapped so the app can offer it from its own menu.
 *
 * The browser's own entry point is a small icon in the omnibox, which is not adequate discoverability
 * for the platform this exists to serve: Linux, where there is no desktop build and the installed window
 * is the whole point.
 *
 * `beforeinstallprompt` fires shortly after load when the page meets the install criteria, and that event
 * object is the ONLY handle on the flow - there is no API to ask for it later. So the listener is
 * registered at MODULE SCOPE rather than inside a hook's effect: by the time React has mounted the
 * account menu the event may already have fired, and a missed event means the row never appears at all.
 * That is also why there is a subscriber set here instead of component state - the event belongs to the
 * module, and any number of components may want to know about it.
 */

type InstallEvent = Event & { prompt: () => Promise<unknown> };

let deferred: InstallEvent | null = null;
const subscribers = new Set<() => void>();

function announce(): void {
  for (const notify of subscribers) notify();
}

if (typeof window !== "undefined") {
  window.addEventListener("beforeinstallprompt", (e) => {
    // Suppress Chromium's own mini-infobar so the account-menu row is the single install affordance.
    e.preventDefault();
    deferred = e as InstallEvent;
    announce();
  });
  window.addEventListener("appinstalled", () => {
    deferred = null;
    announce();
  });
}

/// True when this document is itself an installed app. `matchMedia` is called optionally because jsdom
/// does not implement it at all - the same reason theme.tsx guards its prefers-color-scheme query.
function isInstalledWindow(): boolean {
  return window.matchMedia?.("(display-mode: standalone)").matches === true;
}

export function useInstallPrompt(): { canInstall: boolean; install: () => void } {
  const [offered, setOffered] = useState(deferred !== null);

  useEffect(() => {
    const notify = () => setOffered(deferred !== null);
    subscribers.add(notify);
    // The event can land between this component rendering and this effect running.
    notify();
    return () => {
      subscribers.delete(notify);
    };
  }, []);

  const install = () => {
    const event = deferred;
    if (!event) return;
    // Single-use: Chromium rejects a second prompt() on the same event, so drop it before prompting and
    // let the row disappear rather than leaving a control that silently does nothing.
    deferred = null;
    announce();
    void event.prompt();
  };

  return { canInstall: offered && !isElectron && !isInstalledWindow(), install };
}
