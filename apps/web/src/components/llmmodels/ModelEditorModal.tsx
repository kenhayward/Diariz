import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import HelpButton from "../HelpButton";
import type { LlmModel } from "../../lib/types";
import ParameterPanel, { type ParameterLayer } from "./ParameterPanel";
import { GROUPS } from "./parameterSchema";

interface Props {
  /// Null when adding a model rather than editing one.
  model: LlmModel | null;
  allModels: LlmModel[];
  onClose: () => void;
  onSaved: () => void;
}

type Layers = Record<string, ParameterLayer>;

/// Parses the stored JSON per group into editable layers. A malformed row becomes an empty layer rather
/// than breaking the editor - the admin can then overwrite it, which is the only useful recovery.
function toLayers(model: LlmModel | null): Layers {
  const layers: Layers = {};
  for (const g of GROUPS) {
    const raw = model?.parameters[g.key];
    if (!raw) {
      layers[g.key] = {};
      continue;
    }
    try {
      const parsed = JSON.parse(raw);
      layers[g.key] = parsed && typeof parsed === "object" ? parsed : {};
    } catch {
      layers[g.key] = {};
    }
  }
  return layers;
}

/// Only groups with at least one override are sent. An empty layer would create a row that decides nothing
/// while looking, in the database, exactly like a deliberate set of overrides.
function toWire(layers: Layers): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [group, layer] of Object.entries(layers))
    if (Object.keys(layer).length > 0) out[group] = JSON.stringify(layer);
  return out;
}

export default function ModelEditorModal({ model, allModels, onClose, onSaved }: Props) {
  const { t } = useTranslation("account");
  const [name, setName] = useState(model?.name ?? "");
  const [apiBase, setApiBase] = useState(model?.apiBase ?? "");
  const [contextLength, setContextLength] = useState(model?.contextLength ?? 8192);
  /// Undefined means "not touched": the modal is never given the stored key, so it must omit the field
  /// rather than send "", which the API reads as "clear the key".
  const [apiKey, setApiKey] = useState<string | undefined>(undefined);
  const [layers, setLayers] = useState<Layers>(() => toLayers(model));
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  /// Copies another model's parameter layers into the open editor. Deliberately does NOT copy name,
  /// endpoint or key: those are what make an entry distinct, and copying them would produce a duplicate
  /// pointing at someone else's server. Nothing is persisted until Save.
  function copyFrom(sourceId: string) {
    const source = allModels.find((m) => m.id === sourceId);
    if (source) setLayers(toLayers(source));
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

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/40 p-4">
      <div className="w-full max-w-3xl rounded bg-white p-4 shadow-lg dark:bg-gray-900">
        <h2 className="mb-3 flex items-center gap-1 text-base font-medium text-gray-800 dark:text-gray-100">
          {model ? t("llmModelsEditTitle", { name: model.name }) : t("llmModelsAddTitle")}
          <HelpButton topic="ai-model-parameters" />
        </h2>

        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsName")}</span>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="openai/gpt-oss-20b"
              className="w-full rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-900"
            />
            <span className="mt-0.5 block text-xs text-gray-400 dark:text-gray-500">
              {t("llmModelsNameHint")}
            </span>
          </label>

          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsEndpoint")}</span>
            <input
              value={apiBase}
              onChange={(e) => setApiBase(e.target.value)}
              placeholder="http://localhost:1234/v1"
              className="w-full rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-900"
            />
          </label>

          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsApiKey")}</span>
            <input
              type="password"
              value={apiKey ?? ""}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder={model?.hasApiKey ? t("llmModelsApiKeyStored") : t("llmModelsKeyNone")}
              className="w-full rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-900"
            />
            <span className="mt-0.5 block text-xs text-gray-400 dark:text-gray-500">
              {t("llmModelsApiKeyHint")}
            </span>
          </label>

          <label className="block text-sm">
            <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsContextLength")}</span>
            <input
              type="number"
              min={1}
              value={contextLength}
              onChange={(e) => setContextLength(parseInt(e.target.value, 10) || 0)}
              className="w-full rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-900"
            />
          </label>
        </div>

        <label className="mt-3 block text-sm">
          <span className="mb-1 block text-gray-600 dark:text-gray-300">{t("llmModelsCopyFrom")}</span>
          <select
            defaultValue=""
            onChange={(e) => copyFrom(e.target.value)}
            className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-900"
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
          <span className="ml-2 text-xs text-gray-400 dark:text-gray-500">
            {t("llmModelsCopyFromHint")}
          </span>
        </label>

        <div className="mt-4 grid gap-3">
          {GROUPS.map((g) => (
            <ParameterPanel
              key={g.key}
              groupKey={g.key}
              label={g.label}
              layer={layers[g.key] ?? {}}
              // A group inherits from this model's own Defaults panel, which is what the layer walk does
              // on the server; the Defaults panel itself has only the app defaults below it, which the
              // editor does not know, so it shows nothing rather than guessing.
              inherited={g.key === "ModelBase" ? {} : (layers.ModelBase ?? {})}
              onChange={(layer) => setLayers({ ...layers, [g.key]: layer })}
            />
          ))}
        </div>

        {error && <p className="mt-3 text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1 text-sm dark:border-gray-700"
          >
            {t("llmModelsCancel")}
          </button>
          <button
            type="button"
            onClick={save}
            disabled={saving}
            className="rounded bg-indigo-600 px-3 py-1 text-sm text-white disabled:opacity-50"
          >
            {t("llmModelsSave")}
          </button>
        </div>
      </div>
    </div>
  );
}
