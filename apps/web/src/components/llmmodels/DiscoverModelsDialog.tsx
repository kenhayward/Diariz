import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import type { DiscoverModelsResult } from "../../lib/types";

interface Props {
  onClose: () => void;
  /// Fired once models have been created, so the page can refetch its listing.
  onImported: () => void;
}

/// Adds every chat model on one endpoint in a single pass.
///
/// Two steps rather than one press, deliberately. Pointing this at a server with forty loaded models and no
/// confirmation would be a lot to undo by hand, and the list is also where an administrator learns which
/// models the endpoint declined to report a context window for - a number they will otherwise never think
/// to check, and which silently sizes both the chat dial and the real context budget.
export default function DiscoverModelsDialog({ onClose, onImported }: Props) {
  const { t } = useTranslation("account");
  const [apiBase, setApiBase] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [found, setFound] = useState<DiscoverModelsResult | null>(null);
  const [chosen, setChosen] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function discover() {
    if (!apiBase.trim()) return;
    setBusy(true);
    setError(null);
    setFound(null);
    try {
      const result = await api.discoverModels({ apiBase: apiBase.trim(), apiKey: apiKey.trim() || null });
      setFound(result);
      // NOTHING new is pre-ticked. The ticks describe what the main panel already holds, so adding a model
      // is always a deliberate choice - pre-ticking every model on a busy server made pressing Add all a
      // forty-model commitment taken by default. Select all is one press away for the bulk case.
      setChosen(new Set());
    } catch (e) {
      setError(apiErrorMessage(e, t("llmModelsDiscoverError")));
    } finally {
      setBusy(false);
    }
  }

  async function importChosen() {
    setBusy(true);
    setError(null);
    try {
      await api.importModels({
        // The RESOLVED endpoint the server reported, not what was typed - an address without its /v1 is
        // corrected during discovery, and importing against the typed one is what created models that
        // could never be called.
        apiBase: found?.apiBase ?? apiBase.trim(),
        apiKey: apiKey.trim() || null,
        names: [...chosen],
      });
      onImported();
    } catch (e) {
      setError(apiErrorMessage(e, t("llmModelsImportFailed")));
    } finally {
      setBusy(false);
    }
  }

  function toggle(id: string) {
    setChosen((prev) => {
      const next = new Set(prev);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  /// Every model that CAN be added - an existing one is rendered ticked to show it is already in the panel,
  /// but it is not a candidate and must never reach the import.
  const addable = (found?.models ?? []).filter((m) => !m.alreadyExists);

  const field =
    "w-full rounded-md border border-gray-300 px-2 py-1 text-xs dark:border-gray-700 dark:bg-gray-900";

  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-lg rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900">
        <header className="border-b border-gray-200 px-5 py-3 dark:border-gray-800">
          <h2 className="text-[15px] font-semibold text-gray-900 dark:text-gray-100">
            {t("llmModelsAddAllTitle")}
          </h2>
          <p className="mt-1 text-[12px] text-gray-500 dark:text-gray-400">{t("llmModelsAddAllIntro")}</p>
        </header>

        <div className="grid gap-3 px-5 py-4 sm:grid-cols-2">
          <label className="block text-[11.5px]">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsEndpoint")}</span>
            <input
              value={apiBase}
              onChange={(e) => setApiBase(e.target.value)}
              placeholder="http://localhost:1234/v1"
              className={field}
            />
          </label>
          <label className="block text-[11.5px]">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsApiKey")}</span>
            <input
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder={t("llmModelsKeyNone")}
              className={field}
            />
          </label>
        </div>

        {error && (
          <p className="mx-5 mb-3 rounded border border-red-200 bg-red-50 p-2 text-xs text-red-700 dark:border-red-900 dark:bg-red-900/20 dark:text-red-300">
            {error}
          </p>
        )}

        {found !== null && (
          <div className="max-h-64 overflow-y-auto border-t border-gray-200 px-5 py-3 dark:border-gray-800">
            <div className="mb-2 flex items-baseline justify-between gap-3">
              <p className="min-w-0 text-[11px] text-gray-500 dark:text-gray-400">
                {t("llmModelsWillUseEndpoint", { endpoint: found.apiBase })}
              </p>
              {addable.length > 0 && (
                <button
                  type="button"
                  onClick={() =>
                    setChosen(
                      chosen.size === addable.length ? new Set() : new Set(addable.map((m) => m.id)),
                    )
                  }
                  className="shrink-0 text-[11px] text-indigo-600 hover:underline dark:text-indigo-400"
                >
                  {chosen.size === addable.length ? t("llmModelsClearAll") : t("llmModelsSelectAll")}
                </button>
              )}
            </div>
            {found.models.length === 0 ? (
              <p className="text-xs text-gray-500 dark:text-gray-400">{t("llmModelsDiscoverEmpty")}</p>
            ) : (
              <ul className="space-y-1.5">
                {found.models.map((m) => (
                  <li key={m.id} className="flex items-start gap-2 text-[12px]">
                    <input
                      type="checkbox"
                      aria-label={m.id}
                      checked={m.alreadyExists || chosen.has(m.id)}
                      disabled={m.alreadyExists}
                      onChange={() => toggle(m.id)}
                      className="mt-0.5 size-3.5 shrink-0 accent-indigo-600 disabled:opacity-60"
                    />
                    <span className="min-w-0">
                      <span className="block truncate text-gray-900 dark:text-gray-100">{m.id}</span>
                      <span className="block text-[11px] tabular-nums text-gray-500 dark:text-gray-400">
                        {m.contextLength.toLocaleString()} ctx
                        {!m.contextLengthReported && ` · ${t("llmModelsCtxNotReported")}`}
                        {m.alreadyExists && ` · ${t("llmModelsAlreadyDefined")}`}
                      </span>
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        <footer className="flex items-center justify-end gap-2 border-t border-gray-200 px-5 py-3 dark:border-gray-800">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-gray-300 px-2.5 py-1 text-xs dark:border-gray-700"
          >
            {t("llmModelsCancel")}
          </button>
          {found !== null && found.models.length > 0 ? (
            // Keyed on having found something, not on having ticked something: nothing is ticked by default
            // now, and flipping back to Discover at that moment would look like the search had been lost.
            // A search that found nothing keeps Discover, so the endpoint can be corrected and retried.
            <button
              type="button"
              onClick={importChosen}
              disabled={busy || chosen.size === 0}
              className="rounded-md bg-indigo-600 px-3 py-1 text-xs text-white disabled:opacity-60"
            >
              {t("llmModelsAddCount", { count: chosen.size })}
            </button>
          ) : (
            <button
              type="button"
              onClick={discover}
              disabled={busy || !apiBase.trim()}
              className="rounded-md bg-indigo-600 px-3 py-1 text-xs text-white disabled:opacity-60"
            >
              {t("llmModelsDiscover")}
            </button>
          )}
        </footer>
      </div>
    </div>
  );
}
