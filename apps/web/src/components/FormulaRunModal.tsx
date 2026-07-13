import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import type { Formula, FormulaResult } from "../lib/types";
import { useAuth } from "../auth";
import FlaskIcon from "./FlaskIcon";

/// "Run formula" picker: loads every formula the caller can run (their Personal ones + enabled
/// Platform/Diariz ones - see api.listFormulas), grouped under a scope heading, with a type-ahead filter
/// (modeled on AddMemberTypeahead). Picking one runs it against the recording; on success the parent is
/// notified (refresh + select the new result) and the modal closes. A run failure is surfaced through the
/// parent's error display (onError) and the modal stays open so the user can retry or pick another formula.
export default function FormulaRunModal({
  recordingId,
  onClose,
  onRun,
  onError,
  onManageFormulas,
  onFindShared,
}: {
  recordingId: string;
  onClose: () => void;
  onRun: (result: FormulaResult) => void;
  onError: (msg: string) => void;
  /// Opens Preferences on the Formulas tab. Optional: that tab lands in Task 9, so callers that can't yet
  /// reach it may omit this - the link still renders, it just becomes a no-op.
  onManageFormulas?: () => void;
  /// Opens the "Find shared formulas" discovery browser. Optional; the button is a no-op when omitted.
  onFindShared?: () => void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const { id: myId } = useAuth();
  const { data: formulas = [], isLoading, error: loadError } = useQuery({
    queryKey: ["formulas"],
    queryFn: api.listFormulas,
  });
  const [query, setQuery] = useState("");
  const [running, setRunning] = useState<string | null>(null);
  // A run failure (400 not-configured / 502 / 504 / generic) is shown INLINE here - the page-level banner
  // sits under this modal's backdrop, so delegating there alone would leave the user with no feedback.
  const [runError, setRunError] = useState<string | null>(null);

  // Escape closes the picker (it also closes on a backdrop click). Mirrors EditActionModal/CalendarLinkModal.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const q = query.trim().toLowerCase();
  const filtered = q
    ? formulas.filter(
        (f) => f.name.toLowerCase().includes(q) || (f.description ?? "").toLowerCase().includes(q),
      )
    : formulas;

  // Four groups: Diariz, Platform, the caller's own Personal formulas, and Shared (Personal formulas owned
  // by someone else - added via the discovery browser).
  const groups = useMemo(() => {
    const g = (key: string, items: Formula[]) => ({ scope: key, items });
    return [
      g("Diariz", filtered.filter((f) => f.scope === "Diariz")),
      g("Platform", filtered.filter((f) => f.scope === "Platform")),
      g("Personal", filtered.filter((f) => f.scope === "Personal" && f.ownerUserId === myId)),
      g("Shared", filtered.filter((f) => f.scope === "Personal" && f.ownerUserId !== myId)),
    ].filter((x) => x.items.length > 0);
  }, [filtered, myId]);

  const scopeLabel: Record<string, string> = {
    Diariz: t("workspace:formulaScopeDiariz"),
    Platform: t("workspace:formulaScopePlatform"),
    Personal: t("workspace:formulaScopePersonal"),
    Shared: t("workspace:formulaScopeShared"),
  };

  async function run(formula: Formula) {
    setRunning(formula.id);
    setRunError(null);
    try {
      const result = await api.runFormula(recordingId, formula.id);
      onRun(result);
      onClose();
    } catch (e) {
      const msg = apiErrorMessage(e, t("workspace:errRunFormula"));
      setRunError(msg); // shown inline (visible above the backdrop); the modal stays open for a retry
      onError(msg); // also surface it on the page banner for consistency with other actions
    } finally {
      setRunning(null);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("workspace:formulaRunModalTitle")}
        className="flex max-h-[80vh] w-full max-w-md flex-col rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-3 flex items-center gap-2 text-base font-semibold dark:text-gray-100">
          <FlaskIcon />
          {t("workspace:formulaRunModalTitle")}
        </h2>

        <input
          role="searchbox"
          aria-label={t("workspace:formulaSearchPlaceholder")}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder={t("workspace:formulaSearchPlaceholder")}
          className="mb-3 w-full rounded border px-2 py-1.5 text-sm outline-none dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />

        {runError && (
          <p className="mb-3 rounded bg-red-50 p-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">
            {runError}
          </p>
        )}

        <div className="min-h-0 flex-1 overflow-y-auto">
          {isLoading ? (
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
          ) : loadError ? (
            <p className="text-sm text-red-600 dark:text-red-400">
              {apiErrorMessage(loadError, t("workspace:errLoadFormulas"))}
            </p>
          ) : groups.length === 0 ? (
            <p className="text-sm text-gray-500 dark:text-gray-400">
              {formulas.length === 0 ? t("workspace:noFormulasAvailable") : t("workspace:noFormulaMatches")}
            </p>
          ) : (
            groups.map((g) => (
              <div key={g.scope} className="mb-3">
                <h3 className="mb-1 text-xs font-semibold tracking-wide text-gray-400 uppercase dark:text-gray-500">
                  {scopeLabel[g.scope]}
                </h3>
                <div className="space-y-0.5">
                  {g.items.map((f) => (
                    <button
                      key={f.id}
                      type="button"
                      disabled={running !== null}
                      onClick={() => void run(f)}
                      className="flex w-full items-center justify-between gap-2 rounded px-2 py-1.5 text-left text-sm hover:bg-gray-50 disabled:opacity-50 dark:text-gray-200 dark:hover:bg-gray-800"
                    >
                      <span className="min-w-0">
                        <span className="block truncate font-medium">{f.name}</span>
                        {f.description && (
                          <span className="block truncate text-xs text-gray-400 dark:text-gray-500">{f.description}</span>
                        )}
                      </span>
                      {running === f.id && (
                        <span
                          className="h-3 w-3 shrink-0 animate-spin rounded-full border-2 border-gray-300 border-t-transparent"
                          aria-hidden
                        />
                      )}
                    </button>
                  ))}
                </div>
              </div>
            ))
          )}
        </div>

        <div className="mt-3 flex items-center justify-between border-t pt-3 dark:border-gray-700">
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={() => onManageFormulas?.()}
              className="text-sm text-blue-600 hover:underline dark:text-blue-400"
            >
              {t("workspace:manageFormulas")}
            </button>
            <button
              type="button"
              onClick={() => onFindShared?.()}
              className="text-sm text-blue-600 hover:underline dark:text-blue-400"
            >
              {t("workspace:findSharedFormulas")}
            </button>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
        </div>
      </div>
    </div>
  );
}
