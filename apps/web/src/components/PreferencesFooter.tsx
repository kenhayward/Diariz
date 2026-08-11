import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

export type FooterSaveStatus = "idle" | "unsaved" | "saved";

/// The primitives the footer paints from. `onSave` is deliberately NOT one of them: a tab's handler is a
/// fresh closure on every render, and putting it in the registration effect's dependency list would
/// re-register on every render forever. It is carried in a ref instead (see `usePreferencesFooter`).
export interface FooterSaveState {
  dirty: boolean;
  busy: boolean;
  status: FooterSaveStatus;
  error: string | null;
}

/// Split into two contexts on purpose. The api half is memoised once and never changes identity, so a tab
/// can depend on it in an effect; the state half changes on every registration. One combined context
/// would make the api value change whenever the state did, re-running the tab's effect, which would
/// register again and loop.
const FooterApiCtx = createContext<{
  register: (state: FooterSaveState | null) => void;
  saveRef: React.MutableRefObject<(() => void) | null>;
} | null>(null);
const FooterStateCtx = createContext<FooterSaveState | null>(null);

/// Holds whichever tab has opted into the modal footer. Exactly one tab is mounted at a time, so a single
/// slot is enough - there is no registry and no ordering to resolve.
export function PreferencesFooterProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<FooterSaveState | null>(null);
  const saveRef = useRef<(() => void) | null>(null);
  const api = useMemo(() => ({ register: setState, saveRef }), []);
  return (
    <FooterApiCtx.Provider value={api}>
      <FooterStateCtx.Provider value={state}>{children}</FooterStateCtx.Provider>
    </FooterApiCtx.Provider>
  );
}

/// Opt this tab into the shared footer: the modal paints its Save button and status line. A tab that never
/// calls this keeps its own in-body Save and sees the plain Close-only footer, which is the case for five
/// of the six tabs. Outside a provider this is a no-op, so a tab still renders standalone in a test.
///
/// The argument is nullable, and it must still be called on every render (never behind an `if`) - a tab
/// with an async load (e.g. a settings query still in flight) passes `null` for the renders before its
/// data has arrived, which registers nothing and leaves the plain Close-only footer up. That keeps a
/// live-looking Save button from ever appearing over a blank/default-seeded panel, where clicking it
/// would PUT the component's hardcoded initial state instead of the user's real settings. Passing `null`
/// this way (rather than always registering and relying on an internal `active` flag) means "not ready
/// yet" is visible at the call site, not buried inside the values being passed.
export function usePreferencesFooter(reg: (FooterSaveState & { onSave: () => void }) | null) {
  const api = useContext(FooterApiCtx);
  const dirty = reg?.dirty ?? false;
  const busy = reg?.busy ?? false;
  const status = reg?.status ?? "idle";
  const error = reg?.error ?? null;
  const active = reg !== null;

  // Refreshed on every render, and deliberately not in the effect below - the footer must call the
  // handler as it is now, not the one that existed when the tab first registered.
  useEffect(() => {
    if (api && reg) api.saveRef.current = reg.onSave;
  });

  useEffect(() => {
    api?.register(active ? { dirty, busy, status, error } : null);
  }, [api, active, dirty, busy, status, error]);

  // Unmount only, so switching tabs restores the plain footer. Kept separate from the effect above,
  // whose cleanup would otherwise blank the footer on every value change.
  useEffect(() => () => api?.register(null), [api]);
}

/// The modal's footer: status on the left, Close then Save changes on the right. Save is present only
/// while a tab has registered.
export function PreferencesFooterBar({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("account");
  const state = useContext(FooterStateCtx);
  const api = useContext(FooterApiCtx);

  const statusText =
    state === null || state.error ? null : state.status === "unsaved" ? t("unsavedChanges") : state.status === "saved" ? t("saved") : null;

  return (
    <div className="flex items-center justify-between gap-4 border-t px-5 py-3 dark:border-gray-700">
      <div className="min-w-0 truncate text-[13px]">
        {state?.error ? (
          <span className="text-red-600 dark:text-red-400">{state.error}</span>
        ) : (
          <span className="text-gray-500 dark:text-gray-400">{statusText}</span>
        )}
      </div>
      <div className="flex shrink-0 items-center gap-2">
        <button
          type="button"
          onClick={onClose}
          className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
        >
          {t("common:close")}
        </button>
        {state && (
          <button
            type="button"
            onClick={() => api?.saveRef.current?.()}
            disabled={state.busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {state.busy ? t("common:saving") : t("saveChanges")}
          </button>
        )}
      </div>
    </div>
  );
}
