import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import type { LlmModel } from "../lib/types";
import RoutingMatrix from "../components/llmmodels/RoutingMatrix";
import type { TestState } from "../components/llmmodels/TestRail";
import ModelEditorDrawer from "../components/llmmodels/ModelEditorDrawer";
import DiscoverModelsDialog from "../components/llmmodels/DiscoverModelsDialog";

interface Props {
  /// Rendered inside the settings modal rather than as its own route: drops the top bar and the
  /// full-height shell, which the host provides, and routes the usage-log link through `onOpenUsageLog`
  /// instead of navigating. The route still exists for a pasted or bookmarked link.
  embedded?: boolean;
  onOpenUsageLog?: (query: string) => void;
}

/// Platform-Administrator-only editor for the models every LLM call is routed to, at /admin/llm-models
/// behind the app login (see App.tsx). `RequireAuth` there only checks that someone is signed in, so the
/// permission gate lives here - the same arrangement as LlmUsage.
export default function LlmModels({ embedded = false, onOpenUsageLog }: Props = {}) {
  const { t } = useTranslation("account");
  const { isPlatformAdmin } = useAuth();
  const queryClient = useQueryClient();

  const [editing, setEditing] = useState<LlmModel | null>(null);
  const [adding, setAdding] = useState(false);
  const [discovering, setDiscovering] = useState(false);
  const [error, setError] = useState<string | null>(null);
  /// Model id -> its last connection test. Lives here rather than in the matrix so it survives the
  /// re-render a routing write causes, and so Test all can drive it.
  const [tests, setTests] = useState<Record<string, TestState>>({});

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

  // The bottom of the parameter layer stack. It comes from server configuration rather than the database,
  // so it never changes while the page is open - hence no invalidation anywhere below.
  const defaultsQuery = useQuery({
    queryKey: ["llm-model-defaults"],
    queryFn: () => api.getLlmModelDefaults(),
    enabled: isPlatformAdmin,
    staleTime: Infinity,
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

  /// Its own mutation rather than part of saveAssignments: routing replaces the whole assignment set,
  /// while this flips one model's own flag. Sending them together would make a checkbox click rewrite
  /// every route.
  const setChatEnabled = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      api.setModelChatEnabled(id, enabled),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["llm-models"] }),
    onError: (e) => setError(apiErrorMessage(e, t("llmModelsAssignError"))),
  });

  /// A row test runs the model's SAVED parameters against its own Defaults scope - it answers "can we
  /// reach this at all", which is a different question from the drawer's per-call-type test.
  async function runTest(model: LlmModel) {
    setTests((prev) => ({ ...prev, [model.id]: { status: "running" } }));
    try {
      const result = await api.testModel(model.id, { group: "ModelBase", parameters: model.parameters });
      setTests((prev) => ({ ...prev, [model.id]: { status: "done", result } }));
    } catch (e) {
      setTests((prev) => ({ ...prev, [model.id]: { status: "idle" } }));
      setError(apiErrorMessage(e, t("llmModelsLoadError")));
    }
  }

  /// One at a time, on purpose. These are real calls to real endpoints, and several models commonly share
  /// one server - firing them together would measure the queue rather than the models.
  async function runTestAll() {
    for (const model of models) await runTest(model);
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

  function closeDrawer() {
    setEditing(null);
    setAdding(false);
  }

  if (!isPlatformAdmin) {
    return (
      <Shell embedded={embedded}>
        <p className="p-6 text-sm text-gray-600 dark:text-gray-300">{t("llmModelsForbidden")}</p>
      </Shell>
    );
  }

  return (
    <Shell embedded={embedded}>

      {/* No centring wrapper: the matrix needs the full width, and a max-w-5xl would scroll it
          horizontally on a display that has room to spare. */}
      <div className="px-6 pb-7 pt-5">
        <div className="mb-4 flex items-start justify-between gap-6">
          <div>
            <h2 className="text-[15px] font-semibold text-gray-900 dark:text-gray-100">
              {t("llmModelsRoutingTitle")}
            </h2>
            <p className="mt-1 max-w-[620px] text-[12.5px] leading-relaxed text-gray-500 dark:text-gray-400">
              {t("llmModelsIntro")}
            </p>
          </div>
          <div className="flex shrink-0 gap-2">
            <button
              type="button"
              onClick={() => setAdding(true)}
              className="whitespace-nowrap rounded-md bg-indigo-600 px-3 py-1.5 text-[12.5px] text-white"
            >
              {t("llmModelsAdd")}
            </button>
            <button
              type="button"
              onClick={() => setDiscovering(true)}
              className="whitespace-nowrap rounded-md border border-gray-300 px-3 py-1.5 text-[12.5px] dark:border-gray-700"
            >
              {t("llmModelsAddAll")}
            </button>
          </div>
        </div>

        {error && (
          <p className="mb-3 rounded border border-red-200 bg-red-50 p-2 text-sm text-red-700 dark:border-red-900 dark:bg-red-900/20 dark:text-red-300">
            {error}
          </p>
        )}

        {modelsQuery.isError ? (
          <p className="text-sm text-red-600 dark:text-red-400">{t("llmModelsLoadError")}</p>
        ) : models.length === 0 ? (
          <div className="rounded-lg border border-gray-200 p-6 dark:border-gray-800">
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("llmModelsEmpty")}</p>
            {/* A one-time migration aid. The API refuses a second call, so offering it once models exist
                would be offering an action that can only fail. */}
            {!modelsQuery.isLoading && (
              <button
                type="button"
                onClick={createFromEnvironment}
                className="mt-3 rounded-md border border-gray-300 px-3 py-1 text-xs dark:border-gray-700"
              >
                {t("llmModelsCreateFromEnv")}
              </button>
            )}
          </div>
        ) : (
          <RoutingMatrix
            models={models}
            assignments={assignments}
            defaultModelId={defaultModelId}
            onRoute={(next) => saveAssignments.mutate(next)}
            onEdit={setEditing}
            tests={tests}
            onTest={runTest}
            onTestAll={runTestAll}
            onChatEnabledChange={(id, enabled) => setChatEnabled.mutate({ id, enabled })}
          />
        )}
      </div>

      {discovering && (
        <DiscoverModelsDialog
          onClose={() => setDiscovering(false)}
          onImported={() => {
            setDiscovering(false);
            queryClient.invalidateQueries({ queryKey: ["llm-models"] });
          }}
        />
      )}

      {(editing || adding) && (
        <ModelEditorDrawer
          model={editing}
          allModels={models}
          defaults={defaultsQuery.data ?? {}}
          isDefaultModel={editing !== null && editing.id === defaultModelId}
          onClose={closeDrawer}
          onSaved={() => {
            closeDrawer();
            queryClient.invalidateQueries({ queryKey: ["llm-models"] });
          }}
          onDeleted={() => {
            closeDrawer();
            queryClient.invalidateQueries({ queryKey: ["llm-models"] });
            queryClient.invalidateQueries({ queryKey: ["llm-assignments"] });
          }}
          onOpenUsageLog={onOpenUsageLog}
        />
      )}
    </Shell>
  );
}

/// The page chrome, or none of it when the panel is hosted in a modal that already provides its own.
function Shell({ embedded, children }: { embedded: boolean; children: React.ReactNode }) {
  if (embedded) return <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">{children}</div>;
  return (
    <div className="flex h-screen flex-col overflow-y-auto">
      <TopBar />
      {children}
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
