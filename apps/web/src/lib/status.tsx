import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from "react";
import type { StatusTone } from "./statusBar";

export interface StatusMessage {
  text: string;
  tone: StatusTone;
}

interface StatusContextValue {
  status: StatusMessage | null;
  /// Show a status-bar message. `progress`/`error` stay until replaced (or explicitly cleared with null);
  /// other tones auto-clear after a few seconds. Pass `sticky` to override the auto-clear either way.
  setStatus: (text: string | null, tone?: StatusTone, opts?: { sticky?: boolean }) => void;
}

const AUTO_CLEAR_MS = 6000;

const StatusContext = createContext<StatusContextValue>({ status: null, setStatus: () => {} });

/// App-wide transient status feed for the bottom status bar. Holds a single current message; components push
/// to it (uploads, chat, and the recording page's client-only actions) and the StatusBar renders it.
export function StatusProvider({ children }: { children: ReactNode }) {
  const [status, setStatusState] = useState<StatusMessage | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const setStatus = useCallback<StatusContextValue["setStatus"]>((text, tone = "info", opts) => {
    if (timer.current) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    if (text == null || text === "") {
      setStatusState(null);
      return;
    }
    setStatusState({ text, tone });
    const sticky = opts?.sticky ?? (tone === "progress" || tone === "error");
    if (!sticky) timer.current = setTimeout(() => setStatusState(null), AUTO_CLEAR_MS);
  }, []);

  return <StatusContext.Provider value={{ status, setStatus }}>{children}</StatusContext.Provider>;
}

export function useStatus(): StatusContextValue {
  return useContext(StatusContext);
}
