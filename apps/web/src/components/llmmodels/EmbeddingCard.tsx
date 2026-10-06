import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import type { EmbeddingSettings, EmbeddingTestResult } from "../../lib/types";

/// Shared with LlmModels, which invalidates it whenever routing or a model changes: while embeddings follow the
/// default model, either can move them.
export const EMBEDDING_SETTINGS_KEY = ["embedding-settings"] as const;

/// The embedding endpoint on the AI models page (issue #836). Embeddings are not a routable call group - the
/// model and dimension are pinned to the vector column - so they get a card of their own rather than a column
/// in the matrix: where they go, which level decided that, and a test that checks the vector size too.
export default function EmbeddingCard() {
  const { t } = useTranslation("account");
  const query = useQuery({ queryKey: EMBEDDING_SETTINGS_KEY, queryFn: () => api.getEmbeddingSettings() });
  // Held here, not in the form: a save that changes the endpoint re-keys the form, which would wipe it.
  const [saved, setSaved] = useState<string | null>(null);

  return (
    <section className="mt-8 rounded-lg border border-gray-200 p-4 dark:border-gray-800">
      <h2 className="text-[15px] font-semibold text-gray-900 dark:text-gray-100">{t("embeddingTitle")}</h2>
      <p className="mt-1 max-w-[620px] text-[12.5px] leading-relaxed text-gray-500 dark:text-gray-400">
        {t("embeddingIntro")}
      </p>
      {query.isError ? (
        <p className="mt-3 text-sm text-red-600 dark:text-red-400">{t("embeddingLoadError")}</p>
      ) : query.data ? (
        // Keyed on the saved endpoint so the form starts from what is saved, and again after every save,
        // without an effect copying server state into local state.
        <EmbeddingForm key={query.data.savedApiBase ?? ""} settings={query.data} saved={saved} onSaved={setSaved} />
      ) : null}
    </section>
  );
}

function EmbeddingForm({
  settings,
  saved,
  onSaved,
}: {
  settings: EmbeddingSettings;
  saved: string | null;
  onSaved: (message: string | null) => void;
}) {
  const { t } = useTranslation("account");
  const queryClient = useQueryClient();
  const [apiBase, setApiBase] = useState(settings.savedApiBase ?? "");
  const [apiKey, setApiKey] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [test, setTest] = useState<EmbeddingTestResult | null>(null);

  const save = useMutation({
    mutationFn: (body: { apiBase: string | null; apiKey: string | null }) => api.saveEmbeddingSettings(body),
    onMutate: () => {
      setError(null);
      onSaved(null);
      setTest(null);
    },
    onSuccess: (result) => {
      queryClient.setQueryData(EMBEDDING_SETTINGS_KEY, result.settings);
      onSaved(t("embeddingSaved", { count: result.reindexQueued }));
    },
    onError: (e) => setError(apiErrorMessage(e, t("embeddingSaveError"))),
  });

  const runTest = useMutation({
    mutationFn: () => api.testEmbedding(),
    onMutate: () => setTest(null),
    onSuccess: setTest,
    onError: (e) => setError(apiErrorMessage(e, t("embeddingTestError"))),
  });

  const trimmed = apiBase.trim();
  const busy = save.isPending || runTest.isPending;

  return (
    <div className="mt-3 space-y-3 text-[12.5px]">
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
        <dt className="text-gray-500 dark:text-gray-400">{t("embeddingSendingTo")}</dt>
        <dd className="break-all font-mono text-gray-900 dark:text-gray-100">
          {settings.effectiveApiBase ?? t("embeddingNowhere")}
        </dd>
        <dt className="text-gray-500 dark:text-gray-400">{t("embeddingDecidedBy")}</dt>
        <dd className="text-gray-900 dark:text-gray-100">{t(`embeddingSource${settings.source}`)}</dd>
        <dt className="text-gray-500 dark:text-gray-400">{t("embeddingModel")}</dt>
        <dd className="text-gray-900 dark:text-gray-100">
          {t("embeddingModelValue", { model: settings.model, dimension: settings.dimension })}
        </dd>
      </dl>

      {(settings.source === "DefaultModel" || settings.source === "None") && (
        <p
          role="alert"
          className="rounded border border-amber-200 bg-amber-50 p-2 text-amber-800 dark:border-amber-900 dark:bg-amber-900/20 dark:text-amber-300"
        >
          {settings.source === "DefaultModel"
            ? t("embeddingWarnDefaultModel", { model: settings.model })
            : t("embeddingWarnNone")}
        </p>
      )}

      <div className="grid max-w-[620px] gap-2">
        <label className="grid gap-1">
          <span className="text-gray-600 dark:text-gray-300">{t("embeddingEndpointLabel")}</span>
          <input
            value={apiBase}
            onChange={(e) => setApiBase(e.target.value)}
            placeholder={settings.serverApiBase ?? "http://host:1234/v1"}
            className="rounded border border-gray-300 px-2 py-1 font-mono dark:border-gray-700 dark:bg-gray-900"
          />
        </label>
        <label className="grid gap-1">
          <span className="text-gray-600 dark:text-gray-300">{t("embeddingKeyLabel")}</span>
          <input
            type="password"
            autoComplete="off"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            placeholder={settings.savedHasApiKey ? t("embeddingKeyKeep") : t("embeddingKeyOptional")}
            className="rounded border border-gray-300 px-2 py-1 dark:border-gray-700 dark:bg-gray-900"
          />
        </label>
      </div>

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          disabled={busy}
          // A blank key box keeps the saved key; the separate Remove key button is the only way to drop it.
          onClick={() => save.mutate({ apiBase: trimmed || null, apiKey: apiKey ? apiKey : null })}
          className="rounded-md bg-indigo-600 px-3 py-1.5 text-white disabled:opacity-40"
        >
          {t("embeddingSave")}
        </button>
        {settings.savedApiBase && (
          <button
            type="button"
            disabled={busy}
            onClick={() => save.mutate({ apiBase: null, apiKey: null })}
            className="rounded-md border border-gray-300 px-3 py-1.5 disabled:opacity-40 dark:border-gray-700"
          >
            {t("embeddingClear")}
          </button>
        )}
        {settings.savedApiBase && settings.savedHasApiKey && (
          <button
            type="button"
            disabled={busy}
            onClick={() => save.mutate({ apiBase: settings.savedApiBase, apiKey: "" })}
            className="rounded-md border border-gray-300 px-3 py-1.5 disabled:opacity-40 dark:border-gray-700"
          >
            {t("embeddingRemoveKey")}
          </button>
        )}
        <button
          type="button"
          disabled={busy}
          onClick={() => runTest.mutate()}
          className="rounded-md border border-gray-300 px-3 py-1.5 disabled:opacity-40 dark:border-gray-700"
        >
          {runTest.isPending ? t("embeddingTesting") : t("embeddingTest")}
        </button>
      </div>

      {saved && <p className="text-green-700 dark:text-green-400">{saved}</p>}
      {error && <p className="text-red-600 dark:text-red-400">{error}</p>}
      {test && (
        <p
          data-testid="embedding-test-result"
          className={test.ok ? "text-green-700 dark:text-green-400" : "text-red-600 dark:text-red-400"}
        >
          {test.ok
            ? t("embeddingTestOk", { dimension: test.dimension, ms: test.durationMs })
            : test.statusCode && test.statusCode !== 200
              ? t("embeddingTestFailedStatus", { status: test.statusCode, error: test.error })
              : t("embeddingTestFailed", { error: test.error })}
        </p>
      )}
    </div>
  );
}
