import { useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";
import { sectionPathLabel } from "../lib/sectionTree";
import type { SectionDto } from "../lib/types";
import FolderPicker from "./FolderPicker";

/// Elements a real browser would put in the Tab order. Good enough for this dialog's own controls (plain
/// buttons and one text input - see `FolderPicker.tsx`'s doc comment for why its rows are built this way);
/// not a general-purpose selector.
const FOCUSABLE_SELECTOR = 'button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])';

/// Dialog chrome around `FolderPicker`, which is rendered unchanged - the row semantics stay the ones the
/// left nav teaches (a row body drills, a separate control chooses), and `MoveToSectionModal`, the other
/// consumer, is untouched by this.
///
/// The dialog holds no choice of its own: `onSelect` fires straight through to the panel, and Done only
/// closes. The panel's Save is still the only thing that persists anything.
export default function FolderPickerModal({
  sections,
  selectedId,
  onSelect,
  onClose,
}: {
  sections: SectionDto[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation("workspace");
  const boxRef = useRef<HTMLDivElement>(null);

  // The filter box is the intended keyboard path through a long tree, so start there rather than making
  // the user tab past the header to reach it.
  useEffect(() => {
    boxRef.current?.querySelector<HTMLInputElement>('input[type="text"]')?.focus();
  }, []);

  const chosen = useMemo(
    () => sectionPathLabel(sections, selectedId, t("ungrouped")),
    [sections, selectedId, t],
  );

  // `PreferencesModal` listens for Escape on `document`. React's synthetic `stopPropagation()` also calls
  // the native event's `stopPropagation()`, so stopping it here keeps a bare Escape from ever reaching
  // that listener - it closes this dialog and leaves Preferences open. The same mechanism means an Escape
  // handled inside `FolderPicker` - clearing a non-empty filter - is stopped there too, so it never reaches
  // `PreferencesModal`'s listener either.
  //
  // `aria-modal="true"` below claims a modality that only holds if Tab actually stays inside - so this
  // also traps Tab/Shift-Tab at the dialog's own first/last focusable control, wrapping instead of
  // escaping into whatever Preferences renders behind the overlay.
  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "Escape") {
      e.stopPropagation();
      e.nativeEvent.stopImmediatePropagation();
      onClose();
      return;
    }
    if (e.key !== "Tab") return;
    const items = Array.from(boxRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR) ?? []);
    if (items.length === 0) return;
    const first = items[0];
    const last = items[items.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  return (
    // The backdrop does NOT close on click, matching every other dialog in this app. No handler of its own
    // - it is never itself a keydown target (it holds no focusable content directly), so a copy here would
    // be dead code; the dialog div below is where every key actually lands.
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-gray-950/55 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t("folderPickerTitle")}
        ref={boxRef}
        onKeyDown={onKeyDown}
        className="flex max-h-full w-[420px] flex-col overflow-hidden rounded-[10px] border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-start justify-between gap-3 px-4 pt-3.5 pb-2.5">
          <div className="min-w-0">
            <h2 className="text-[15px] font-semibold dark:text-gray-100">{t("folderPickerTitle")}</h2>
            <p className="text-xs text-gray-500 dark:text-gray-400">{t("folderPickerSubtitle")}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("folderPickerCloseAria")}
            className="shrink-0 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
          >
            ✕
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-4">
          <FolderPicker sections={sections} selectedId={selectedId} onSelect={onSelect} />
        </div>

        <div className="flex items-center justify-between gap-3 border-t px-4 py-2.5 dark:border-gray-700">
          <span className="min-w-0 truncate text-xs text-gray-500 dark:text-gray-400">
            {t("folderPickerChosen", { path: chosen })}
          </span>
          <button
            type="button"
            onClick={onClose}
            className="shrink-0 rounded bg-gray-900 px-3.5 py-1.5 text-[13px] font-medium text-white dark:bg-gray-100 dark:text-gray-900"
          >
            {t("folderPickerDone")}
          </button>
        </div>
      </div>
    </div>
  );
}
