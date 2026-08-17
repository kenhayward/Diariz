import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import type { LlmModel } from "../lib/types";
import ModelList from "../components/llmmodels/ModelList";
import ModelEditorModal from "../components/llmmodels/ModelEditorModal";
import { ASSIGNABLE_GROUPS } from "../components/llmmodels/parameterSchema";

/// Platform-Administrator-only editor for the models every LLM call is routed to, at /admin/llm-models
/// behind the app login (see App.tsx). `RequireAuth` there only checks that someone is signed in, so the
/// permission gate lives here - the same arrangement as LlmUsage.
export default function LlmModels() {
  const { t } = useTranslation("account");
  const { isPlatformAdmin } = useAuth();
  const queryClient = useQueryClient();

  const [editing, setEditing] = useState<LlmModel | null>(null);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // `enabled` keeps a non-admin from issuing the request at all: a refusal that still fetched would put
  // every configured endpoint name into the network log of someone not allowed to see them.
  const modelsQuery = useQuery({
    queryKey: ["llm-models"],
    queryFn: () => api.listModels(),
    enabled: isPlatformAdmin,
  });

  const assignmentsQuery = useQuery({
    queryKey: ["llm-assignments"],
    queryFn: () => api.getLlmAssignments(),
    enabled: isPlatformAdmin,
  });

  const models = modelsQuery.data ?? [];
  const assignments = assignmentsQuery.data?.assignments ?? {};
  const defaultModelId = assignmentsQuery.data?.defaultModelId ?? null;

  const saveAssignments = useMutation({
    mutationFn: (next: { defaultModelId: string | null; assignments: Record<string, string> }) =>
      api.setLlmAssignments(next),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["llm-assignments"] }),
    onError: (e) => setError(apiErrorMessage(e, t("llmModelsAssignError"))),
  });

  function assign(groupKey: string, modelId: string) {
    const next = { ...assignments };
    // "" is the empty choice in the select: removing the entry is what makes the group fall back to the
    // default model, so it must delete the key rather than store a blank id.
    if (modelId) next[groupKey] = modelId;
    else delete next[groupKey];
    saveAssignments.mutate({ defaultModelId, assignments: next });
  }

  async function remove(model: LlmModel) {
    setError(null);
    try {
      await api.deleteModel(model.id);
      await queryClient.invalidateQueries({ queryKey: ["llm-models"] });
    } catch (e) {
      // The API refuses while any group or the default still points at it, and says which - surface that
      // verbatim rather than a generic failure, because it names the exact thing to change first.
      setError(apiErrorMessage(e, t("llmModelsDeleteError")));
    }
  }

  async function createFromEnvironment() {
    setError(null);
    try {
      await api.createModelFromEnvironment();
      await queryClient.invalidateQueries({ queryKey: ["llm-models"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("llmModelsImportError")));
    }
  }

  if (!isPlatformAdmin) {
    return (
      <div className="flex h-screen flex-col">
        <TopBar />
        <p className="p-6 text-sm text-gray-600 dark:text-gray-300">
          {t("llmModelsForbidden")}
        </p>
      </div>
    );
  }

  return (
    <div className="flex h-screen flex-col overflow-y-auto">
      <TopBar />

      <div className="mx-auto w-full max-w-5xl p-4">
        <p className="mb-3 text-sm text-gray-600 dark:text-gray-300">
          {t("llmModelsIntro")}
        </p>

        {error && (
          <p className="mb-3 rounded border border-red-200 bg-red-50 p-2 text-sm text-red-700 dark:border-red-900 dark:bg-red-900/20 dark:text-red-300">
            {error}
          </p>
        )}

        <div className="mb-3 flex gap-2">
          <button
            type="button"
            onClick={() => setAdding(true)}
            className="rounded bg-indigo-600 px-3 py-1 text-sm text-white"
          >
            {t("llmModelsAdd")}
          </button>
          {/* A one-time migration aid. The API refuses a second call, so offering it once models exist
              would be offering an action that can only fail. */}
          {!modelsQuery.isLoading && models.length === 0 && (
            <button
              type="button"
              onClick={createFromEnvironment}
              className="rounded border px-3 py-1 text-sm dark:border-gray-700"
            >
              {t("llmModelsCreateFromEnv")}
            </button>
          )}
        </div>

        {modelsQuery.isError ? (
          <p className="text-sm text-red-600 dark:text-red-400">{t("llmModelsLoadError")}</p>
        ) : models.length === 0 ? (
          <p className="text-sm text-gray-500 dark:text-gray-400">
            {t("llmModelsEmpty")}
          </p>
        ) : (
          <ModelList
            models={models}
            assignments={assignments}
            defaultModelId={defaultModelId}
            onEdit={setEditing}
            onDelete={remove}
          />
        )}

        {models.length > 0 && (
          <section className="mt-6">
            <h2 className="mb-1 text-sm font-medium text-gray-800 dark:text-gray-100">{t("llmModelsAssignTitle")}</h2>
            <p className="mb-2 text-xs text-gray-500 dark:text-gray-400">
              {t("llmModelsAssignHint")}
            </p>

            <label className="mb-3 block text-sm">
              <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsDefaultModel")}</span>
              <select
                value={defaultModelId ?? ""}
                onChange={(e) =>
                  saveAssignments.mutate({ defaultModelId: e.target.value || null, assignments })
                }
                className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-900"
              >
                <option value="">{t("llmModelsUseEnvironment")}</option>
                {models.map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name}
                  </option>
                ))}
              </select>
            </label>

            <div className="grid gap-2 sm:grid-cols-2">
              {ASSIGNABLE_GROUPS.map((g) => (
                <label key={g.key} className="block text-sm">
                  <span className="mb-1 block text-gray-600 dark:text-gray-300">{t(g.label)}</span>
                  <select
                    value={assignments[g.key] ?? ""}
                    onChange={(e) => assign(g.key, e.target.value)}
                    className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-900"
                  >
                    <option value="">{t("llmModelsUseDefault")}</option>
                    {models.map((m) => (
                      <option key={m.id} value={m.id}>
                        {m.name}
                      </option>
                    ))}
                  </select>
                </label>
              ))}
            </div>
          </section>
        )}
      </div>

      {(editing || adding) && (
        <ModelEditorModal
          model={editing}
          allModels={models}
          onClose={() => {
            setEditing(null);
            setAdding(false);
          }}
          onSaved={() => {
            setEditing(null);
            setAdding(false);
            queryClient.invalidateQueries({ queryKey: ["llm-models"] });
          }}
        />
      )}
    </div>
  );
}

function TopBar() {
  const { t } = useTranslation("account");
  return (
    <div className="flex items-center gap-3 border-b bg-white px-4 py-2 text-sm dark:border-gray-700 dark:bg-gray-900">
      <Link to="/" className="text-indigo-600 hover:underline dark:text-indigo-400">
        ← {t("apiBackToApp")}
      </Link>
      <span className="font-medium text-gray-700 dark:text-gray-200">{t("llmModelsTitle")}</span>
    </div>
  );
}
