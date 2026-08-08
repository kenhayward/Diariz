import { useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import HelpButton from "../HelpButton";
import type { UserSettings } from "../../lib/types";

/// The model override, in a dialog. It was a whole Preferences tab; it is the least-used thing in here,
/// and everything it holds is an *exception* to a platform default that already works.
///
/// The tri-state `apiKey` is carried across from the old section unchanged and is the reason this cannot
/// be a plain controlled input: `null` means untouched (leave the stored key alone), `""` means clear it,
/// and a value means replace it. Collapsing those three into one would silently wipe a stored key every
/// time someone opened the dialog and saved without touching the field.
export default function ModelDialog({ data, onClose }: { data: UserSettings; onClose: () => void }) {
  const { t } = useTranslation("account");
  const qc = useQueryClient();

  const [apiBase, setApiBase] = useState(data.apiBase ?? "");
  const [model, setModel] = useState(data.model ?? "");
  const [apiKey, setApiKey] = useState<string | null>(null); // null = untouched, "" = clear, value = set
  const [reasoningEnabled, setReasoningEnabled] = useState(data.reasoningEnabled);
  const [reasoningEffort, setReasoningEffort] = useState(data.reasoningEffort || "medium");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Escape closes, the backdrop does not - matching PreferencesModal, which this sits over.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function onSave() {
    setError(null);
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
      onClose();
    } catch (e) {
      // Keep the dialog open on failure: closing would throw away what they typed.
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  /// "Key overridden - endpoint and model use the platform's", in whatever combination applies. Plain
  /// language beats three ticks, because the answer people want here is "what am I actually running?".
  const overridden = [
    apiBase.trim() ? t("modelFieldEndpoint") : null,
    model.trim() ? t("modelFieldModel") : null,
    apiKey === "" ? null : apiKey !== null || data.hasApiKey ? t("modelFieldKey") : null,
  ].filter(Boolean) as string[];
  const rest = [
    apiBase.trim() ? null : t("modelFieldEndpoint"),
    model.trim() ? null : t("modelFieldModel"),
  ].filter(Boolean) as string[];
  const summary = overridden.length === 0
    ? t("modelOverrideNone")
    : [
        t("modelOverrideSummary", { overridden: overridden.join(", ") }),
        rest.length ? t("modelOverrideRest", { rest: rest.join(" and ") }) : null,
      ].filter(Boolean).join(" · ");

  const field = "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";
  const labelSpan = "mb-1 block text-gray-600 dark:text-gray-300";
  const btn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t("modelDialogTitle")}
        className="flex max-h-[85vh] w-full max-w-[520px] flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center justify-between gap-2 border-b px-4 py-3 dark:border-gray-700">
          <h3 className="text-sm font-semibold dark:text-gray-100">{t("modelDialogTitle")}</h3>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("common:close")}
            className="rounded px-1 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
          >
            ✕
          </button>
        </div>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-3">
          <p className="text-xs text-gray-500 dark:text-gray-400">
            {t("aiIntro")} <HelpButton topic="ai-model-settings" />
          </p>
          {/* Non-login field names + per-field autoComplete="off" stop password managers autofilling these. */}
          <label className="block text-sm">
            <span className={labelSpan}>{t("summaryEndpoint")}</span>
            <input
              name="diariz-summary-endpoint"
              autoComplete="off"
              value={apiBase}
              onChange={(e) => setApiBase(e.target.value)}
              placeholder={data.defaultApiBase ? t("defaultValue", { value: data.defaultApiBase }) : "https://api.openai.com/v1"}
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
              placeholder={data.defaultModel ? t("defaultValue", { value: data.defaultModel }) : "gpt-4o-mini"}
              className={field}
            />
          </label>
          <label className="block text-sm">
            <span className={labelSpan}>
              {t("summaryApiKey")}
              {data.hasApiKey && apiKey === null && (
                <span className="ml-1 text-green-600 dark:text-green-400">{t("keySet")}</span>
              )}
            </span>
            <input
              type="password"
              name="diariz-summary-key"
              autoComplete="new-password"
              value={apiKey ?? ""}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder={data.hasApiKey ? t("keyKeepBlank") : data.serverHasApiKey ? t("keyServerDefault") : "sk-…"}
              className={field}
            />
            {data.hasApiKey && (
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
              // Three options do not need a dropdown, and a segmented control shows all three states at
              // rest rather than hiding two behind a click.
              <div className="mt-2 flex items-center gap-2">
                <span className="text-sm text-gray-600 dark:text-gray-300">{t("reasoningLevel")}</span>
                <div role="radiogroup" aria-label={t("reasoningLevel")} className="flex gap-1">
                  {(["low", "medium", "high"] as const).map((level) => (
                    <button
                      key={level}
                      type="button"
                      role="radio"
                      aria-checked={reasoningEffort === level}
                      onClick={() => setReasoningEffort(level)}
                      className={`rounded px-3 py-[3px] text-xs ${
                        reasoningEffort === level
                          ? "bg-blue-50 text-blue-800 ring-1 ring-blue-200 dark:bg-blue-500/[0.18] dark:text-blue-100 dark:ring-blue-400/45"
                          : "text-gray-600 ring-1 ring-gray-200 hover:bg-gray-50 dark:text-gray-300 dark:ring-gray-700 dark:hover:bg-gray-800"
                      }`}
                    >
                      {t(level === "low" ? "reasoningLow" : level === "medium" ? "reasoningMedium" : "reasoningHigh")}
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>

        <div className="flex items-center justify-between gap-3 border-t px-4 py-3 dark:border-gray-700">
          <span className="min-w-0 truncate text-[11px] text-gray-400 dark:text-gray-500">{summary}</span>
          <div className="flex shrink-0 items-center gap-2">
            {error && <span className="text-xs text-red-600 dark:text-red-400">{error}</span>}
            <button type="button" onClick={onClose} className={btn}>
              {t("common:cancel")}
            </button>
            <button
              type="button"
              onClick={() => void onSave()}
              disabled={busy}
              className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
            >
              {busy ? t("common:saving") : t("common:save")}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
