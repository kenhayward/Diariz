import { useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { WebhookCreated } from "../lib/types";

type Provider = "zapier" | "n8n" | null;

/// The Preferences "Automations" section: the guided CREATE flow for outbound webhooks (send meeting events to
/// Zapier / n8n / Make / anything that can accept a webhook). Shown only when the platform has webhooks enabled
/// (the parent gates on profile.webhooksEnabled). Plain-language checkboxes pick which events fire it; provider
/// hint tabs help the user find the right URL to paste from their tool. The signing secret is shown exactly
/// once, right after creation - it is never retrievable again. Listing/status/test/delete existing automations
/// is a later task; this section only creates new ones.
export default function AutomationsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();

  const EVENTS: { key: string; label: string }[] = [
    { key: "recording.created", label: t("evtRecordingCreated") },
    { key: "recording.transcribed", label: t("evtRecordingTranscribed") },
    { key: "recording.transcription_failed", label: t("evtRecordingFailed") },
    { key: "formula_result.completed", label: t("evtFormulaCompleted") },
    { key: "formula_result.failed", label: t("evtFormulaFailed") },
  ];

  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [provider, setProvider] = useState<Provider>(null);
  const [created, setCreated] = useState<WebhookCreated | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const eventTypes = EVENTS.map((e) => e.key).filter((key) => selected[key]);

  const copy = (text: string) => void navigator.clipboard?.writeText(text);

  async function create() {
    setError(null);
    setBusy(true);
    try {
      const result = await api.createWebhook({
        name: name.trim() || t("automationDefaultName"),
        url,
        eventTypes,
      });
      setCreated(result);
      setName("");
      setUrl("");
      setSelected({});
      qc.invalidateQueries({ queryKey: ["webhooks"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("automationCreateError")));
    } finally {
      setBusy(false);
    }
  }

  const btn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";
  // Two brand-name toggles (proper nouns, not translated); with neither selected we show the generic hint.
  const providerHint =
    provider === "zapier" ? t("automationHintZapier") : provider === "n8n" ? t("automationHintN8n") : t("automationHintOther");

  return (
    <div className="space-y-3">
      <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("automationsTitle")}</span>
      <p className="text-xs text-gray-400 dark:text-gray-500">{t("automationsIntro")}</p>

      <div>
        <span className="mb-1 block text-xs font-medium text-gray-600 dark:text-gray-300">
          {t("automationEventsHeading")}
        </span>
        <div className="space-y-1">
          {EVENTS.map((evt) => (
            <label key={evt.key} className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                aria-label={evt.label}
                checked={selected[evt.key] ?? false}
                onChange={(e) => setSelected((s) => ({ ...s, [evt.key]: e.target.checked }))}
              />
              {evt.label}
            </label>
          ))}
        </div>
      </div>

      <div>
        <label className="mb-1 block text-xs font-medium text-gray-600 dark:text-gray-300">
          {t("automationDestinationUrl")}
        </label>
        <input
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          aria-label={t("automationDestinationUrl")}
          placeholder="https://hooks.zapier.com/..."
          className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <div className="mt-2 flex gap-1" role="tablist">
          {(["zapier", "n8n"] as const).map((id) => (
            <button
              key={id}
              type="button"
              role="tab"
              aria-selected={provider === id}
              onClick={() => setProvider((p) => (p === id ? null : id))}
              className={`rounded px-2 py-1 text-xs ${
                provider === id
                  ? "bg-gray-900 text-white dark:bg-gray-100 dark:text-gray-900"
                  : "border text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-300 dark:hover:bg-gray-800"
              }`}
            >
              {id === "zapier" ? "Zapier" : "n8n"}
            </button>
          ))}
        </div>
        <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{providerHint}</p>
      </div>

      <input
        value={name}
        onChange={(e) => setName(e.target.value)}
        placeholder={t("automationDefaultName")}
        aria-label={t("automationDefaultName")}
        className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />

      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={create}
          disabled={busy || !url || eventTypes.length === 0}
          className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {t("automationCreate")}
        </button>
        {error && <span className="text-sm text-red-600 dark:text-red-400">{error}</span>}
      </div>

      {created && (
        <div className="mt-2 rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
          <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{t("automationSecretOnce")}</p>
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">
              {created.secret}
            </code>
            <button type="button" onClick={() => copy(created.secret)} className={btn}>
              {t("apiCopyToken")}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
