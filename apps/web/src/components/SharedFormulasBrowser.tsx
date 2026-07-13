import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import type { SharedFormula } from "../lib/types";
import { initialsFromName } from "../lib/initials";
import Avatar from "./Avatar";
import FlaskIcon from "./FlaskIcon";

/// Discovery browser for formulas other users have shared. Lists each shared formula with the sharer's
/// avatar + name, its description, a View/Hide toggle for the read-only prompt, and an Add/Remove button
/// that subscribes/unsubscribes the caller (a live link, not a copy). Adding or removing invalidates both
/// the discovery list and the run picker's ["formulas"] query so the "Shared" group updates. It carries no
/// unsaved work, so it closes on Escape or a backdrop click.
export default function SharedFormulasBrowser({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation(["workspace", "common"]);
  const qc = useQueryClient();
  const [query, setQuery] = useState("");
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);

  const { data: shared = [], isLoading, error: loadError } = useQuery({
    queryKey: ["shared-formulas"],
    queryFn: api.listSharedFormulas,
  });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const mutation = useMutation({
    mutationFn: ({ id, add }: { id: string; add: boolean }) =>
      add ? api.subscribeFormula(id) : api.unsubscribeFormula(id),
    onSuccess: () => {
      // The run picker's Shared group reads ["formulas"]; keep both in sync.
      qc.invalidateQueries({ queryKey: ["shared-formulas"] });
      qc.invalidateQueries({ queryKey: ["formulas"] });
    },
    onError: (e) => setError(apiErrorMessage(e, t("workspace:errSubscribeFormula"))),
  });

  function toggleAdd(sf: SharedFormula) {
    setError(null);
    mutation.mutate({ id: sf.formula.id, add: !sf.alreadyAdded });
  }

  const q = query.trim().toLowerCase();
  const filtered = q
    ? shared.filter(
        (sf) =>
          sf.formula.name.toLowerCase().includes(q) ||
          (sf.formula.description ?? "").toLowerCase().includes(q) ||
          (sf.ownerName ?? "").toLowerCase().includes(q),
      )
    : shared;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("workspace:sharedFormulasTitle")}
        className="flex max-h-[80vh] w-full max-w-lg flex-col rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-3 flex items-center gap-2 text-base font-semibold dark:text-gray-100">
          <FlaskIcon />
          {t("workspace:sharedFormulasTitle")}
        </h2>

        <input
          role="searchbox"
          aria-label={t("workspace:formulaSearchPlaceholder")}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder={t("workspace:formulaSearchPlaceholder")}
          className="mb-3 w-full rounded border px-2 py-1.5 text-sm outline-none dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />

        {error && (
          <p className="mb-3 rounded bg-red-50 p-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">
            {error}
          </p>
        )}

        <div className="min-h-0 flex-1 overflow-y-auto">
          {isLoading ? (
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
          ) : loadError ? (
            <p className="text-sm text-red-600 dark:text-red-400">
              {apiErrorMessage(loadError, t("workspace:errLoadSharedFormulas"))}
            </p>
          ) : filtered.length === 0 ? (
            <p className="text-sm text-gray-500 dark:text-gray-400">
              {shared.length === 0 ? t("workspace:sharedFormulasEmpty") : t("workspace:noSharedFormulaMatches")}
            </p>
          ) : (
            <div className="space-y-2">
              {filtered.map((sf) => {
                const f = sf.formula;
                const isOpen = !!expanded[f.id];
                return (
                  <div key={f.id} className="rounded border p-3 dark:border-gray-700">
                    <div className="flex items-start justify-between gap-3">
                      <div className="flex min-w-0 items-start gap-2">
                        <Avatar size="xs" initials={initialsFromName(sf.ownerName)} pictureUrl={sf.ownerPictureUrl} />
                        <div className="min-w-0">
                          <div className="truncate text-sm font-medium dark:text-gray-100">{f.name}</div>
                          <div className="truncate text-xs text-gray-400 dark:text-gray-500">
                            {t("workspace:sharedFormulaBy", { name: sf.ownerName ?? "" })}
                          </div>
                          {f.description && (
                            <div className="mt-0.5 text-xs text-gray-500 dark:text-gray-400">{f.description}</div>
                          )}
                        </div>
                      </div>
                      <button
                        type="button"
                        disabled={mutation.isPending}
                        onClick={() => toggleAdd(sf)}
                        className={
                          sf.alreadyAdded
                            ? "shrink-0 rounded border px-2.5 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                            : "shrink-0 rounded bg-blue-600 px-2.5 py-1 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                        }
                      >
                        {sf.alreadyAdded ? t("workspace:removeSharedFormula") : t("workspace:addSharedFormula")}
                      </button>
                    </div>

                    <button
                      type="button"
                      onClick={() => setExpanded((s) => ({ ...s, [f.id]: !s[f.id] }))}
                      className="mt-2 text-xs text-blue-600 hover:underline dark:text-blue-400"
                    >
                      {isOpen ? t("workspace:hideFormula") : t("workspace:viewFormula")}
                    </button>
                    {isOpen && (
                      <pre className="mt-2 max-h-48 overflow-y-auto rounded bg-gray-50 p-2 text-xs whitespace-pre-wrap dark:bg-gray-800 dark:text-gray-200">
                        {f.prompt}
                      </pre>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="mt-3 flex justify-end border-t pt-3 dark:border-gray-700">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </div>
  );
}
