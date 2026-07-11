import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";

/// Model Settings tab: the user's own OpenAI-compatible summarisation endpoint / model / key, plus the
/// reasoning toggle + level. Self-contained (its own Save persists just these fields); the write is tri-state
/// so it never disturbs the Chat Tools or Recordings preferences.
export default function AiSettingsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });

  const [apiBase, setApiBase] = useState("");
  const [model, setModel] = useState("");
  const [apiKey, setApiKey] = useState<string | null>(null); // null = untouched, "" = clear, value = set
  const [reasoningEnabled, setReasoningEnabled] = useState(false);
  const [reasoningEffort, setReasoningEffort] = useState("medium");
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (data) {
      setApiBase(data.apiBase ?? "");
      setModel(data.model ?? "");
      setReasoningEnabled(data.reasoningEnabled);
      setReasoningEffort(data.reasoningEffort || "medium");
    }
  }, [data]);

  // Render only once the settings have loaded, so an early edit can't be overwritten by the arriving values.
  if (!data) return null;

  async function onSave() {
    setError(null);
    setSaved(false);
    setBusy(true);
    try {
      await api.updateUserSettings({
        apiBase: apiBase.trim(), // "" clears the override (falls back to the server default)
        model: model.trim(),
        apiKey, // null leaves it unchanged; "" clears; a value sets it
        reasoningEnabled,
        reasoningEffort,
      });
      qc.invalidateQueries({ queryKey: ["user-settings"] });
      setApiKey(null); // a fresh key was saved (or left untouched); reset to "untouched"
      setSaved(true);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const field = "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";
  const labelSpan = "mb-1 block text-gray-600 dark:text-gray-300";

  return (
    <div className="space-y-3">
      <p className="text-xs text-gray-500 dark:text-gray-400">{t("aiIntro")}</p>
      {/* Non-login field names + per-field autoComplete="off" stop password managers autofilling these. */}
      <label className="block text-sm">
        <span className={labelSpan}>{t("summaryEndpoint")}</span>
        <input
          name="diariz-summary-endpoint"
          autoComplete="off"
          value={apiBase}
          onChange={(e) => setApiBase(e.target.value)}
          placeholder={data?.defaultApiBase ? t("defaultValue", { value: data.defaultApiBase }) : "https://api.openai.com/v1"}
          className={field}
        />
      </label>
      <label className="block text-sm">
        <span className={labelSpan}>{t("summaryModel")}</span>
        <input
          name="diariz-summary-model"
          autoComplete="off"
          value={model}
          onChange={(e) => setModel(e.target.value)}
          placeholder={data?.defaultModel ? t("defaultValue", { value: data.defaultModel }) : "gpt-4o-mini"}
          className={field}
        />
      </label>
      <label className="block text-sm">
        <span className={labelSpan}>
          {t("summaryApiKey")}
          {data?.hasApiKey && apiKey === null && (
            <span className="ml-1 text-green-600 dark:text-green-400">{t("keySet")}</span>
          )}
        </span>
        <input
          type="password"
          name="diariz-summary-key"
          autoComplete="new-password"
          value={apiKey ?? ""}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder={
            data?.hasApiKey ? t("keyKeepBlank") : data?.serverHasApiKey ? t("keyServerDefault") : "sk-…"
          }
          className={field}
        />
        {data?.hasApiKey && (
          <button
            type="button"
            onClick={() => setApiKey("")}
            className="mt-1 text-xs text-red-600 hover:underline dark:text-red-400"
          >
            {t("clearStoredKey")}
          </button>
        )}
      </label>
      {apiKey === "" && <p className="text-xs text-amber-600 dark:text-amber-400">{t("keyWillClear")}</p>}

      {/* Reasoning: on/off + effort level (only for reasoning-capable models). */}
      <div className="border-t pt-3 dark:border-gray-700">
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={reasoningEnabled} onChange={(e) => setReasoningEnabled(e.target.checked)} />
          <span className="font-medium text-gray-700 dark:text-gray-200">{t("reasoningEnabled")}</span>
        </label>
        <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("reasoningHint")}</p>
        {reasoningEnabled && (
          <label className="mt-2 block text-sm">
            <span className={labelSpan}>{t("reasoningLevel")}</span>
            <select
              value={reasoningEffort}
              onChange={(e) => setReasoningEffort(e.target.value)}
              aria-label={t("reasoningLevel")}
              className={field}
            >
              <option value="low">{t("reasoningLow")}</option>
              <option value="medium">{t("reasoningMedium")}</option>
              <option value="high">{t("reasoningHigh")}</option>
            </select>
          </label>
        )}
      </div>

      <div className="flex items-center gap-3 border-t pt-3 dark:border-gray-700">
        <button
          type="button"
          onClick={onSave}
          disabled={busy}
          className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {busy ? t("common:saving") : t("common:save")}
        </button>
        {error && <span className="text-sm text-red-600 dark:text-red-400">{error}</span>}
        {saved && !error && <span className="text-sm text-green-600 dark:text-green-400">{t("profileSaved")}</span>}
      </div>
    </div>
  );
}
