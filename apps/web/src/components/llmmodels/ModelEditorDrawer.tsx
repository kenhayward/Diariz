import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import HelpButton from "../HelpButton";
import type { LlmModel } from "../../lib/types";
import ParameterGrid from "./ParameterGrid";
import TestRail, { type TestState } from "./TestRail";
import { buildRequestPreview, resolveInherited } from "./requestPreview";
import { GROUPS, type ParameterLayer, type ParameterValue } from "./parameterSchema";

interface Props {
  /// Null when adding a model rather than editing one.
  model: LlmModel | null;
  allModels: LlmModel[];
  /// The application defaults, group -> layer JSON, exactly as the API returns them.
  defaults: Record<string, string>;
  isDefaultModel: boolean;
  onClose: () => void;
  onSaved: () => void;
  onDeleted: () => void;
}

type Layers = Record<string, ParameterLayer>;

/// Parses stored layer JSON per group. A malformed row becomes an empty layer rather than breaking the
/// editor - the admin can then overwrite it, which is the only useful recovery.
function parseLayers(raw: Record<string, string> | undefined, fillEveryGroup: boolean): Layers {
  const layers: Layers = {};
  for (const g of GROUPS) {
    const json = raw?.[g.key];
    if (!json) {
      if (fillEveryGroup) layers[g.key] = {};
      continue;
    }
    try {
      const parsed = JSON.parse(json);
      layers[g.key] = parsed && typeof parsed === "object" ? parsed : {};
    } catch {
      layers[g.key] = {};
    }
  }
  return layers;
}

function toLayers(model: LlmModel | null): Layers {
  return parseLayers(model?.parameters, true);
}

/// Only groups with at least one override are sent. An empty layer would create a row that decides nothing
/// while looking, in the database, exactly like a deliberate set of overrides.
function toWire(layers: Layers): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [group, layer] of Object.entries(layers))
    if (Object.keys(layer).length > 0) out[group] = JSON.stringify(layer);
  return out;
}

const BUTTON = "rounded-md border border-gray-300 px-2.5 py-1 text-xs dark:border-gray-700";

export default function ModelEditorDrawer({
  model, allModels, defaults, isDefaultModel, onClose, onSaved, onDeleted,
}: Props) {
  const { t } = useTranslation("account");
  const [name, setName] = useState(model?.name ?? "");
  const [apiBase, setApiBase] = useState(model?.apiBase ?? "");
  const [contextLength, setContextLength] = useState(model?.contextLength ?? 8192);
  /// Undefined means "not touched": the drawer is never given the stored key, so it must omit the field
  /// rather than send "", which the API reads as "clear the key".
  const [apiKey, setApiKey] = useState<string | undefined>(undefined);
  const [layers, setLayers] = useState<Layers>(() => toLayers(model));
  const [tab, setTab] = useState<string>("ModelBase");
  const [showConnection, setShowConnection] = useState(model === null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  /// Per tab, because the results are not comparable across call types: each ran with different
  /// parameters, so one shared slot would show a number belonging to a tab the admin has left.
  const [tests, setTests] = useState<Record<string, TestState>>({});

  const appDefaults = useMemo(() => parseLayers(defaults, false), [defaults]);
  const initial = useMemo(() => JSON.stringify(toLayers(model)), [model]);
  const dirty =
    JSON.stringify(layers) !== initial ||
    apiKey !== undefined ||
    name !== (model?.name ?? "") ||
    apiBase !== (model?.apiBase ?? "") ||
    contextLength !== (model?.contextLength ?? 8192);

  const group = GROUPS.find((g) => g.key === tab) ?? GROUPS[0];
  const layer = layers[tab] ?? {};
  const overrides = Object.keys(layer).length;
  const inherited = useMemo(() => resolveInherited(layers, appDefaults, tab), [layers, appDefaults, tab]);
  const preview = useMemo(
    () => buildRequestPreview(name || t("llmModelsAddTitle"), layers, tab, appDefaults),
    [name, layers, tab, appDefaults, t],
  );

  function close() {
    // Nothing here is persisted until Save, so an unsaved drawer is a real loss - but only warn when
    // there is something to lose, or the prompt becomes noise the admin learns to dismiss.
    if (dirty && !window.confirm(t("llmModelsDiscardConfirm"))) return;
    onClose();
  }

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") close();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  /// Copies another model's parameter layers into the open drawer. Deliberately does NOT copy name,
  /// endpoint or key: those are what make an entry distinct, and copying them would produce a duplicate
  /// pointing at someone else's server. Nothing is persisted until Save.
  function copyFrom(sourceId: string) {
    const source = allModels.find((m) => m.id === sourceId);
    if (source) setLayers(toLayers(source));
  }

  /// Runs the test with what is on screen rather than what is stored - testing before saving is the whole
  /// reason the endpoint takes parameters at all.
  async function runTest() {
    if (!model) return;
    setTests((prev) => ({ ...prev, [tab]: { status: "running" } }));
    setError(null);
    try {
      const result = await api.testModel(model.id, { group: tab, parameters: toWire(layers) });
      setTests((prev) => ({ ...prev, [tab]: { status: "done", result } }));
    } catch (e) {
      setTests((prev) => ({ ...prev, [tab]: { status: "idle" } }));
      setError(apiErrorMessage(e, t("llmModelsSaveError")));
    }
  }

  /// Applies a one-click fix from a failed result to the OPEN tab, never to the model's Defaults: the
  /// endpoint rejected the parameter for this call, and narrowing the change to this call is what makes
  /// the fix safe to offer at all.
  function applyFix({ key, value }: { key: string; value: ParameterValue }) {
    const next = { ...(layers[tab] ?? {}) };
    if (value === undefined) delete next[key];
    else next[key] = value;
    setLayers({ ...layers, [tab]: next });
  }

  function resetGroup() {
    if (overrides > 0 && !window.confirm(t("llmParamResetGroupConfirm", { group: t(group.label) }))) return;
    setLayers({ ...layers, [tab]: {} });
  }

  async function save() {
    setSaving(true);
    setError(null);
    try {
      const payload = {
        name: name.trim(),
        apiBase: apiBase.trim(),
        contextLength,
        parameters: toWire(layers),
        ...(apiKey === undefined ? {} : { apiKey }),
      };
      if (model) await api.updateModel(model.id, payload);
      else await api.createModel(payload);
      onSaved();
    } catch (e) {
      setError(apiErrorMessage(e, t("llmModelsSaveError")));
    } finally {
      setSaving(false);
    }
  }

  async function remove() {
    if (!model || !window.confirm(t("llmModelsDeleteConfirm", { name: model.name }))) return;
    setError(null);
    try {
      await api.deleteModel(model.id);
      onDeleted();
    } catch (e) {
      // The API refuses while any group or the default still points at it, and says which - surface that
      // verbatim rather than a generic failure, because it names the exact thing to change first.
      setError(apiErrorMessage(e, t("llmModelsDeleteError")));
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-gray-950/60" onMouseDown={close}>
      <div
        role="dialog"
        aria-label={model ? model.name : t("llmModelsAddTitle")}
        onMouseDown={(e) => e.stopPropagation()}
        className="flex h-full w-full max-w-[min(1096px,100vw-144px)] flex-col border-l border-gray-200 bg-white shadow-2xl dark:border-gray-800 dark:bg-gray-950"
      >
        <header className="flex items-start justify-between gap-3 border-b border-gray-200 px-5 py-3 dark:border-gray-800">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <h2 className="truncate text-[15px] font-semibold tracking-tight text-gray-900 dark:text-gray-100">
                {model ? model.name : t("llmModelsAddTitle")}
              </h2>
              {isDefaultModel && (
                <span className="rounded border border-indigo-200 bg-indigo-50 px-1.5 py-px text-[10.5px] font-semibold text-indigo-700 dark:border-indigo-400/40 dark:bg-indigo-600/20 dark:text-indigo-200">
                  {t("llmModelsRoleDefault")}
                </span>
              )}
              <HelpButton topic="ai-model-parameters" />
            </div>
            {model && (
              <p className="mt-1 truncate text-[11.5px] tabular-nums text-gray-500 dark:text-gray-400">
                {model.apiBase} · {model.contextLength.toLocaleString()} ctx ·{" "}
                {model.hasApiKey ? t("llmModelsApiKeyStored") : t("llmModelsKeyNone")}
              </p>
            )}
          </div>
          <div className="flex shrink-0 items-center gap-2">
            <button type="button" onClick={() => setShowConnection((v) => !v)} className={BUTTON}>
              {t("llmModelsConnection")}
            </button>
            <button
              type="button"
              aria-label={t("llmModelsClose")}
              onClick={close}
              className="rounded-md px-2 py-1 text-sm text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
            >
              {"✕"}
            </button>
          </div>
        </header>

        {showConnection && (
          <ConnectionPanel
            name={name} setName={setName}
            apiBase={apiBase} setApiBase={setApiBase}
            apiKey={apiKey} setApiKey={setApiKey}
            contextLength={contextLength} setContextLength={setContextLength}
            hasApiKey={model?.hasApiKey ?? false}
          />
        )}

        <div
          role="tablist"
          className="flex gap-1 overflow-x-auto border-b border-gray-200 bg-slate-50 px-5 py-2 dark:border-gray-800 dark:bg-gray-950/60"
        >
          {GROUPS.map((g) => {
            const count = Object.keys(layers[g.key] ?? {}).length;
            const active = g.key === tab;
            return (
              <button
                key={g.key}
                type="button"
                role="tab"
                aria-selected={active}
                onClick={() => setTab(g.key)}
                className={`shrink-0 rounded-md px-2.5 py-1 text-xs ${
                  active
                    ? "border border-indigo-200 bg-indigo-50 font-semibold text-indigo-700 dark:border-indigo-400/50 dark:bg-indigo-600/25 dark:text-indigo-100"
                    : "border border-transparent text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
                }`}
              >
                {t(g.short)}
                {count > 0 && <span className="ml-1.5 text-[10px] text-gray-400 dark:text-gray-500">{count}</span>}
              </button>
            );
          })}
        </div>

        <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[minmax(0,1fr)_424px]">
          <div className="min-w-0 overflow-auto px-5 pb-4 pt-3">
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <p className="text-[11.5px] text-gray-500 dark:text-gray-400">
                {tab === "ModelBase" ? t("llmParamBaseHint") : t("llmParamGroupHint", { group: t(group.label) })}
              </p>
              <div className="flex shrink-0 items-center gap-2">
                <label className="flex items-center gap-1.5 text-[11.5px] text-gray-500 dark:text-gray-400">
                  {t("llmModelsCopyFrom")}
                  <select
                    defaultValue=""
                    title={t("llmModelsCopyFromHint")}
                    onChange={(e) => copyFrom(e.target.value)}
                    className="rounded-md border border-gray-300 px-1.5 py-1 text-[11.5px] dark:border-gray-700 dark:bg-gray-900"
                  >
                    <option value="">{t("llmModelsCopyFromChoose")}</option>
                    {allModels
                      .filter((m) => m.id !== model?.id)
                      .map((m) => (
                        <option key={m.id} value={m.id}>
                          {m.name}
                        </option>
                      ))}
                  </select>
                </label>
                <button
                  type="button"
                  onClick={resetGroup}
                  className={`${BUTTON} whitespace-nowrap hover:border-red-300 hover:text-red-600 dark:hover:border-red-900 dark:hover:text-red-400`}
                >
                  {"↺ "}
                  {t("llmParamResetGroup")}
                </button>
              </div>
            </div>

            <ParameterGrid
              groupKey={tab}
              layer={layer}
              inherited={inherited}
              onChange={(next) => setLayers({ ...layers, [tab]: next })}
            />
          </div>

          <TestRail
            group={group.label}
            preview={preview}
            test={tests[tab] ?? { status: "idle" }}
            onRun={model ? runTest : null}
            onFix={applyFix}
            apiBase={model?.apiBase ?? ""}
            modelName={model?.name ?? ""}
          />
        </div>

        {error && (
          <p className="mx-5 mb-2 rounded border border-red-200 bg-red-50 p-2 text-xs text-red-700 dark:border-red-900 dark:bg-red-900/20 dark:text-red-300">
            {error}
          </p>
        )}

        <footer className="flex items-center justify-between gap-3 border-t border-gray-200 px-5 py-2.5 dark:border-gray-800">
          <div className="flex items-center gap-3">
            {model && (
              <button
                type="button"
                onClick={remove}
                className="text-[11.5px] text-red-600 hover:underline dark:text-red-400"
              >
                {t("llmModelsDeleteModel")}
              </button>
            )}
            <span className="text-[11.5px] text-gray-500 dark:text-gray-400">
              {dirty
                ? t("llmDrawerUnsaved", { count: overrides, group: t(group.label) })
                : t("llmDrawerOverrides", { count: overrides, group: t(group.label) })}
            </span>
          </div>
          <div className="flex gap-2">
            <button type="button" onClick={close} className={BUTTON}>
              {t("llmModelsCancel")}
            </button>
            <button
              type="button"
              onClick={save}
              disabled={saving}
              className="rounded-md bg-indigo-600 px-3 py-1 text-xs text-white disabled:opacity-50"
            >
              {t("llmModelsSave")}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}

/// Name, endpoint, key and context length. Secondary now: they are set once when a model is added and
/// rarely touched again, so they sit behind a button rather than above every parameter.
function ConnectionPanel({
  name, setName, apiBase, setApiBase, apiKey, setApiKey, contextLength, setContextLength, hasApiKey,
}: {
  name: string;
  setName: (v: string) => void;
  apiBase: string;
  setApiBase: (v: string) => void;
  apiKey: string | undefined;
  setApiKey: (v: string) => void;
  contextLength: number;
  setContextLength: (v: number) => void;
  hasApiKey: boolean;
}) {
  const { t } = useTranslation("account");
  const field =
    "w-full rounded-md border border-gray-300 px-2 py-1 text-xs dark:border-gray-700 dark:bg-gray-900";

  return (
    <div className="grid gap-3 border-b border-gray-200 px-5 py-3 sm:grid-cols-2 lg:grid-cols-4 dark:border-gray-800">
      <label className="block text-[11.5px]">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsName")}</span>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="openai/gpt-oss-20b" className={field} />
        <span className="mt-0.5 block text-[10.5px] text-gray-400 dark:text-gray-500">{t("llmModelsNameHint")}</span>
      </label>
      <label className="block text-[11.5px]">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsEndpoint")}</span>
        <input value={apiBase} onChange={(e) => setApiBase(e.target.value)} placeholder="http://localhost:1234/v1" className={field} />
      </label>
      <label className="block text-[11.5px]">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsApiKey")}</span>
        <input
          type="password"
          value={apiKey ?? ""}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder={hasApiKey ? t("llmModelsApiKeyStored") : t("llmModelsKeyNone")}
          className={field}
        />
        <span className="mt-0.5 block text-[10.5px] text-gray-400 dark:text-gray-500">{t("llmModelsApiKeyHint")}</span>
      </label>
      <label className="block text-[11.5px]">
        <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsContextLength")}</span>
        <input
          type="number"
          min={1}
          value={contextLength}
          onChange={(e) => setContextLength(parseInt(e.target.value, 10) || 0)}
          className={field}
        />
      </label>
    </div>
  );
}
