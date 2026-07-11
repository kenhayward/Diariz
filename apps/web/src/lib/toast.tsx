import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from "react";

interface ToastItem {
  id: number;
  message: string;
}

interface ToastValue {
  /// Show a transient toast that auto-dismisses after a few seconds.
  showToast: (message: string) => void;
}

// Default no-op so components can call useToast() without a provider (e.g. in unit tests).
const ToastContext = createContext<ToastValue>({ showToast: () => {} });

const TOAST_MS = 4000;

/// A lightweight transient-notification stack, floating bottom-centre. One shared queue so any component can
/// confirm an action (e.g. "Weekly meeting shared to Podcasts.") without wiring its own banner.
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);
  const nextId = useRef(0);

  const showToast = useCallback((message: string) => {
    const id = (nextId.current += 1);
    setToasts((t) => [...t, { id, message }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), TOAST_MS);
  }, []);

  return (
    <ToastContext.Provider value={{ showToast }}>
      {children}
      <div
        className="pointer-events-none fixed inset-x-0 bottom-4 z-[100] flex flex-col items-center gap-2 px-4"
        role="status"
        aria-live="polite"
      >
        {toasts.map((t) => (
          <div
            key={t.id}
            className="pointer-events-auto max-w-md rounded-lg bg-gray-900 px-4 py-2 text-sm text-white shadow-lg dark:bg-gray-100 dark:text-gray-900"
          >
            {t.message}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useToast(): ToastValue {
  return useContext(ToastContext);
}
