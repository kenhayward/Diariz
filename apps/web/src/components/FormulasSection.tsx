import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import type { Formula } from "../lib/types";
import FormulaEditModal from "./FormulaEditModal";

/// Formulas tab of Preferences: manage the caller's own Personal formulas (list/create/edit/delete).
/// Platform/Diariz formulas are managed elsewhere (the Phase 3 admin popup) - this section only ever
/// shows scope === "Personal". Mirrors AiSettingsSection's heading/description layout.
export default function FormulasSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: formulas = [], isLoading } = useQuery({ queryKey: ["formulas"], queryFn: api.listFormulas });
  const personal = formulas.filter((f) => f.scope === "Personal");
  // undefined = editor closed, null = creating a new formula, Formula = editing that one.
  const [editing, setEditing] = useState<Formula | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);

  function invalidate() {
    qc.invalidateQueries({ queryKey: ["formulas"] });
  }

  async function remove(f: Formula) {
    if (!window.confirm(t("confirmDeleteFormula", { name: f.name }))) return;
    setError(null);
    try {
      await api.deleteFormula(f.id);
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("errDeleteFormula")));
    }
  }

  return (
    <div className="space-y-3">
      <h3 className="text-base font-semibold dark:text-gray-100">{t("tabFormulas")}</h3>
      <p className="text-xs text-gray-500 dark:text-gray-400">{t("formulasIntro")}</p>

      {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}

      <div>
        <button
          type="button"
          onClick={() => setEditing(null)}
          className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white dark:bg-gray-100 dark:text-gray-900"
        >
          {t("newFormula")}
        </button>
      </div>

      {isLoading && <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>}
      {!isLoading && personal.length === 0 && (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t("formulasEmpty")}</p>
      )}

      {personal.length > 0 && (
        <ul className="divide-y dark:divide-gray-800">
          {personal.map((f) => (
            <li key={f.id} className="flex items-center justify-between gap-2 py-2">
              <span className="min-w-0">
                <span className="block truncate text-sm font-medium dark:text-gray-100">{f.name}</span>
                {f.description && (
                  <span className="block truncate text-xs text-gray-500 dark:text-gray-400">{f.description}</span>
                )}
              </span>
              <span className="flex shrink-0 gap-2">
                <button
                  type="button"
                  onClick={() => setEditing(f)}
                  className="rounded border px-2 py-1 text-xs dark:border-gray-700 dark:text-gray-200"
                >
                  {t("common:edit")}
                </button>
                <button
                  type="button"
                  onClick={() => remove(f)}
                  className="rounded border border-red-300 px-2 py-1 text-xs text-red-600 dark:border-red-800 dark:text-red-400"
                >
                  {t("common:delete")}
                </button>
              </span>
            </li>
          ))}
        </ul>
      )}

      {editing !== undefined && (
        <FormulaEditModal formula={editing} onClose={() => setEditing(undefined)} onSaved={invalidate} />
      )}
    </div>
  );
}
