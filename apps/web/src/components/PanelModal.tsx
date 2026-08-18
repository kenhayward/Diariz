import { useEffect } from "react";
import { useTranslation } from "react-i18next";

/// A near-full-screen modal for the pages that used to be their own routes: the AI models grid, the LLM
/// usage log, and the API reference.
///
/// They were reached with `<a target="_blank">`, which is what made them a problem rather than a
/// preference: in the installed PWA and the desktop shell that leaves the app entirely and opens the
/// system browser, where the user is not signed in - and all three are behind the app login, so they
/// render nothing useful once you get there. Each is wide (a seven-column routing grid, a usage table, a
/// three-column API reference), so they get the whole viewport minus a margin rather than the centred box
/// the smaller dialogs use.
///
/// Sits at z-[60], the layer this codebase already uses for a dialog opened from a dialog. The model
/// editor drawer inside goes above it at z-[65]; the help popover stays above everything at z-[70].
export default function PanelModal({
  title,
  onClose,
  children,
}: {
  title: string;
  onClose: () => void;
  children: React.ReactNode;
}) {
  const { t } = useTranslation("account");

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      // Only when nothing above has taken the key: the drawer inside handles its own Escape, including
      // its unsaved-changes warning, and closing this from underneath would discard that silently.
      if (e.key === "Escape" && !document.querySelector('[role="dialog"][data-drawer="true"]')) onClose();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-[60] bg-black/40 p-4">
      {/* No backdrop-click close: these panels hold unsaved edits, and a stray click on the margin
          discarding them is the accident the drawer's own warning exists to prevent. */}
      <div
        role="dialog"
        aria-label={title}
        className="flex h-full w-full flex-col overflow-hidden rounded-lg bg-gray-50 shadow-2xl dark:bg-gray-950"
      >
        <div className="flex shrink-0 items-center justify-between gap-3 border-b border-gray-200 bg-white px-4 py-2 dark:border-gray-800 dark:bg-gray-900">
          <h2 className="text-sm font-medium text-gray-700 dark:text-gray-200">{title}</h2>
          <button
            type="button"
            aria-label={t("llmModelsClose")}
            onClick={onClose}
            className="rounded-md px-2 py-1 text-sm text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
          >
            {"✕"}
          </button>
        </div>
        <div className="flex min-h-0 flex-1 flex-col overflow-auto">{children}</div>
      </div>
    </div>
  );
}
