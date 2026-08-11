import { useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";
import { orderedSections } from "../lib/sectionTree";
import type { SectionDto } from "../lib/types";
import FolderPicker from "./FolderPicker";

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

  const chosen = useMemo(() => {
    if (selectedId === null) return t("ungrouped");
    return orderedSections(sections).find((o) => o.section.id === selectedId)?.label ?? t("ungrouped");
  }, [sections, selectedId, t]);

  // `PreferencesModal` listens for Escape on `document`. React delegates from the root container, which is
  // a descendant of `document`, so stopping propagation on the native event here does prevent that
  // listener - one Escape closes this dialog and leaves Preferences open. When the filter box is
  // non-empty `FolderPicker` stops the event first to clear the filter, so that press reaches neither.
  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key !== "Escape") return;
    e.stopPropagation();
    e.nativeEvent.stopImmediatePropagation();
    onClose();
  }

  return (
    // The backdrop does NOT close on click, matching every other dialog in this app.
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-gray-950/55 p-4" onKeyDown={onKeyDown}>
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
