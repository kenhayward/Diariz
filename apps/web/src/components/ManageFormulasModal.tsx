import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import type { Formula } from "../lib/types";
import FormulaEditModal from "./FormulaEditModal";
import FlaskIcon from "./FlaskIcon";

/// Admin-only management of Platform + Diariz (built-in) formulas (ManageFormulas-gated). Mirrors
/// ManageUsersModal's chrome: a fixed-size panel that does NOT close on a backdrop click (Close button
/// or Escape only), so an in-progress edit isn't lost to a stray click. Diariz formulas are seeded, not
/// created here - "New formula" always creates a Platform formula. A Diariz `isBuiltIn` formula can be
/// enabled/disabled and edited, but never deleted (the server also 400s a built-in delete).
export default function ManageFormulasModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { data: formulas = [], isLoading } = useQuery({
    queryKey: ["managed-formulas"],
    queryFn: api.listManagedFormulas,
  });
  // undefined = editor closed, null = creating a new Platform formula, Formula = editing that one.
  const [editing, setEditing] = useState<Formula | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  function invalidate() {
    // The picker used to run formulas reads ["formulas"]; refresh it too so an enable/disable or a saved
    // edit here shows up there without a manual reload.
    void qc.invalidateQueries({ queryKey: ["managed-formulas"] });
    void qc.invalidateQueries({ queryKey: ["formulas"] });
  }

  async function toggleEnabled(f: Formula) {
    setError(null);
    try {
      await api.setFormulaEnabled(f.id, !f.enabled);
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function remove(f: Formula) {
    if (!window.confirm(t("account:confirmDeleteFormula", { name: f.name }))) return;
    setError(null);
    try {
      await api.deleteFormula(f.id);
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("account:errDeleteFormula")));
    }
  }

  // Diariz (seeded) formulas first, then Platform - each group alphabetised by name.
  const sorted = [...formulas].sort((a, b) => {
    if (a.scope !== b.scope) return a.scope === "Diariz" ? -1 : 1;
    return a.name.localeCompare(b.name);
  });

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      {/* Does NOT close on a backdrop click (Close button or Escape only) - prevents accidental dismissal
          mid-edit, mirroring ManageUsersModal. */}
      <div
        role="dialog"
        aria-label={t("formulasTitle")}
        className="flex h-[85vh] w-full max-w-4xl flex-col rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <h2 className="mb-3 flex shrink-0 items-center gap-2 text-base font-semibold dark:text-gray-100">
          <FlaskIcon />
          {t("formulasTitle")}
        </h2>

        {error && <p className="mb-2 shrink-0 text-sm text-red-600 dark:text-red-400">{error}</p>}
        {isLoading && <p className="shrink-0 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>}
        {!isLoading && sorted.length === 0 && (
          <p className="shrink-0 text-sm text-gray-500 dark:text-gray-400">{t("account:formulasEmpty")}</p>
        )}

        <div className="min-h-0 flex-1 overflow-auto">
          <table className="w-full text-sm">
            <thead className="sticky top-0 z-10 bg-white text-left text-xs text-gray-400 dark:bg-gray-900 dark:text-gray-500">
              <tr>
                <th className="py-1 pr-2 font-medium">{t("colFormulaName")}</th>
                <th className="py-1 pr-2 font-medium">{t("colFormulaScope")}</th>
                <th className="py-1 pr-2 font-medium">{t("colFormulaDescription")}</th>
                <th className="py-1 pr-2 font-medium">{t("colFormulaEnabled")}</th>
                <th className="py-1 font-medium">{t("colActions")}</th>
              </tr>
            </thead>
            <tbody className="divide-y dark:divide-gray-800">
              {sorted.map((f) => (
                <tr key={f.id} className="align-top dark:text-gray-200">
                  <td className="py-2 pr-2 font-medium">{f.name}</td>
                  <td className="py-2 pr-2">
                    <ScopeBadge scope={f.scope} />
                  </td>
                  <td className="max-w-xs truncate py-2 pr-2 text-xs text-gray-500 dark:text-gray-400">
                    {f.description}
                  </td>
                  <td className="py-2 pr-2">
                    <input
                      type="checkbox"
                      checked={f.enabled}
                      onChange={() => toggleEnabled(f)}
                      aria-label={t("enabledForAria", { name: f.name })}
                    />
                  </td>
                  <td className="py-2">
                    <span className="flex flex-wrap items-center gap-1.5">
                      <button
                        type="button"
                        onClick={() => setEditing(f)}
                        className="rounded border px-2 py-1 text-xs dark:border-gray-700 dark:text-gray-200"
                      >
                        {t("common:edit")}
                      </button>
                      {f.isBuiltIn ? (
                        <span className="text-[11px] text-gray-400 dark:text-gray-500">{t("builtInHint")}</span>
                      ) : (
                        <button
                          type="button"
                          onClick={() => remove(f)}
                          className="rounded border border-red-300 px-2 py-1 text-xs text-red-600 dark:border-red-800 dark:text-red-400"
                        >
                          {t("common:delete")}
                        </button>
                      )}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="mt-3 flex shrink-0 items-center justify-between border-t pt-3 dark:border-gray-700">
          <button
            type="button"
            onClick={() => setEditing(null)}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white dark:bg-gray-100 dark:text-gray-900"
          >
            {t("account:newFormula")}
          </button>
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
      </div>

      {editing !== undefined && (
        <FormulaEditModal
          formula={editing}
          // Only meaningful on create (editing === null) - an existing formula keeps its own (immutable)
          // scope, which FormulaEditModal ignores once `formula` is set.
          scope={editing === null ? "Platform" : undefined}
          onClose={() => setEditing(undefined)}
          onSaved={invalidate}
        />
      )}
    </div>
  );
}

function ScopeBadge({ scope }: { scope: Formula["scope"] }) {
  const { t } = useTranslation("admin");
  if (scope === "Diariz") {
    return (
      <span className="rounded bg-purple-100 px-1.5 py-0.5 text-[11px] text-purple-800 dark:bg-purple-900/40 dark:text-purple-300">
        {t("scopeDiariz")}
      </span>
    );
  }
  return (
    <span className="rounded bg-blue-100 px-1.5 py-0.5 text-[11px] text-blue-800 dark:bg-blue-900/40 dark:text-blue-300">
      {t("scopePlatform")}
    </span>
  );
}
