import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { ApiTokenCreated, WebhookCreated, WebhookSubscription } from "../lib/types";
import type { TFunction } from "i18next";

type Provider = "zapier" | "n8n" | null;

/// The Preferences "Automations" section: the guided CREATE flow for outbound webhooks (send meeting events to
/// Zapier / n8n / Make / anything that can accept a webhook), plus the management UI for existing automations -
/// a card per webhook (trigger chips, destination host, Active/Paused status), "Send test event", delete, and
/// Re-enable for ones the server auto-disabled after repeated failures. Shown only when the platform has
/// webhooks enabled (the parent gates on profile.webhooksEnabled). Plain-language checkboxes pick which events
/// fire it; provider hint tabs help the user find the right URL to paste from their tool. The signing secret is
/// shown exactly once, right after creation - it is never retrievable again. When a formula event is selected,
/// an inline offer to mint a read-only API token appears (formula callers often need to read results back).
export default function AutomationsSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: webhooks } = useQuery({ queryKey: ["webhooks"], queryFn: api.listWebhooks });

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
  const [tokenCreated, setTokenCreated] = useState<ApiTokenCreated | null>(null);
  const [tokenError, setTokenError] = useState<string | null>(null);

  const eventTypes = EVENTS.map((e) => e.key).filter((key) => selected[key]);
  const wantsFormulaToken = eventTypes.some((key) => key.startsWith("formula_result"));

  const copy = (text: string) => void navigator.clipboard?.writeText(text);

  async function createToken() {
    setTokenError(null);
    try {
      const tok = await api.createApiToken(t("automationTokenName"), { readOnly: true, expiresAt: null });
      setTokenCreated(tok);
    } catch (e) {
      setTokenError(apiErrorMessage(e, t("automationCreateError")));
    }
  }

  async function sendTest(id: string) {
    setError(null);
    try {
      await api.testWebhook(id);
    } catch (e) {
      setError(apiErrorMessage(e, t("automationCreateError")));
    }
  }

  async function remove(id: string) {
    setError(null);
    try {
      await api.deleteWebhook(id);
      qc.invalidateQueries({ queryKey: ["webhooks"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("automationCreateError")));
    }
  }

  async function reenable(hook: WebhookSubscription) {
    setError(null);
    try {
      await api.updateWebhook(hook.id, {
        name: hook.name,
        url: hook.url,
        eventTypes: hook.eventTypes,
        isActive: true,
      });
      qc.invalidateQueries({ queryKey: ["webhooks"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("automationCreateError")));
    }
  }

  const eventLabel = (key: string) => EVENTS.find((e) => e.key === key)?.label ?? key;
  const host = (url: string) => {
    try {
      return new URL(url).host;
    } catch {
      return url;
    }
  };

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
        {wantsFormulaToken && !tokenCreated && (
          <div className="mt-2 rounded border border-blue-200 bg-blue-50 p-2 dark:border-blue-800/60 dark:bg-blue-900/20">
            <p className="text-xs text-blue-800 dark:text-blue-300">{t("automationTokenOffer")}</p>
            <button type="button" onClick={createToken} className={`mt-1 ${btn}`}>
              {t("automationTokenCreate")}
            </button>
            {tokenError && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{tokenError}</p>}
          </div>
        )}
        {tokenCreated && (
          <div className="mt-2 rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
            <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{t("apiTokenOnce")}</p>
            <div className="mt-1 flex items-center gap-2">
              <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">
                {tokenCreated.token}
              </code>
              <button type="button" onClick={() => copy(tokenCreated.token)} className={btn}>
                {t("apiCopyToken")}
              </button>
            </div>
          </div>
        )}
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

      <div className="space-y-2 border-t pt-3 dark:border-gray-700">
        {webhooks?.length === 0 && (
          <p className="text-xs text-gray-400 dark:text-gray-500">{t("automationEmpty")}</p>
        )}
        {webhooks?.map((hook) => (
          <AutomationCard
            key={hook.id}
            hook={hook}
            t={t}
            btn={btn}
            eventLabel={eventLabel}
            host={host}
            onSendTest={sendTest}
            onReenable={reenable}
            onRemove={remove}
          />
        ))}
      </div>
    </div>
  );
}

/// One automation card: status/trigger chips plus a "Recent deliveries" disclosure that lazily fetches this
/// automation's delivery history only once expanded (a per-card hook keeps the fetch/expand state isolated
/// from the parent's variable-length map, rather than calling useQuery directly inside .map).
function AutomationCard({
  hook,
  t,
  btn,
  eventLabel,
  host,
  onSendTest,
  onReenable,
  onRemove,
}: {
  hook: WebhookSubscription;
  t: TFunction;
  btn: string;
  eventLabel: (key: string) => string;
  host: (url: string) => string;
  onSendTest: (id: string) => void;
  onReenable: (hook: WebhookSubscription) => void;
  onRemove: (id: string) => void;
}) {
  const paused = !hook.isActive;
  const [expanded, setExpanded] = useState(false);
  const { data: deliveries, isLoading } = useQuery({
    queryKey: ["webhook-deliveries", hook.id],
    queryFn: () => api.listWebhookDeliveries(hook.id),
    enabled: expanded,
  });

  return (
    <div className="rounded border p-2 dark:border-gray-700">
      <div className="flex items-center justify-between gap-2">
        <span className="truncate text-sm font-medium text-gray-800 dark:text-gray-100">{hook.name}</span>
        <span
          className={`shrink-0 rounded-full px-2 py-0.5 text-xs ${
            paused
              ? "bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300"
              : "bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-300"
          }`}
        >
          {paused ? t("automationPaused") : t("automationActive")}
        </span>
      </div>
      <p className="mt-0.5 text-xs text-gray-400 dark:text-gray-500">{host(hook.url)}</p>
      {paused && (hook.disabledReason || hook.lastStatus) && (
        <p className="mt-0.5 text-[11px] text-red-600 dark:text-red-400">
          {hook.disabledReason ?? hook.lastStatus}
        </p>
      )}
      <div className="mt-1 flex flex-wrap gap-1">
        {hook.eventTypes.map((key) => (
          <span
            key={key}
            className="rounded-full border px-2 py-0.5 text-[11px] text-gray-600 dark:border-gray-700 dark:text-gray-300"
          >
            {eventLabel(key)}
          </span>
        ))}
      </div>
      {hook.lastDeliveryAt && (
        <p className="mt-1 text-[11px] text-gray-400 dark:text-gray-500">
          {t("automationLastDelivered", { when: new Date(hook.lastDeliveryAt).toLocaleString() })}
        </p>
      )}
      <div className="mt-2 flex flex-wrap gap-2">
        <button type="button" onClick={() => onSendTest(hook.id)} className={btn}>
          {t("automationSendTest")}
        </button>
        {paused && (
          <button type="button" onClick={() => onReenable(hook)} className={btn}>
            {t("automationReenable")}
          </button>
        )}
        <button
          type="button"
          onClick={() => setExpanded((e) => !e)}
          aria-expanded={expanded}
          className={btn}
        >
          {t("automationDeliveries")}
        </button>
        <button
          type="button"
          onClick={() => onRemove(hook.id)}
          className="rounded border px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:border-gray-700 dark:text-red-400 dark:hover:bg-red-900/20"
        >
          {t("automationDelete")}
        </button>
      </div>
      {expanded && (
        <div className="mt-2 space-y-1 rounded border border-gray-100 bg-gray-50 p-2 dark:border-gray-800 dark:bg-gray-900/40">
          {!isLoading && !deliveries?.length && (
            <p className="text-[11px] text-gray-400 dark:text-gray-500">{t("automationNoDeliveries")}</p>
          )}
          {deliveries?.map((d) => (
            <div
              key={d.id}
              className="flex items-center justify-between gap-2 text-[11px] text-gray-600 dark:text-gray-300"
            >
              <span className="truncate">{eventLabel(d.eventType)}</span>
              <span className="shrink-0">
                {d.status}
                {d.responseStatus != null && ` (${d.responseStatus})`}
              </span>
              <span className="shrink-0 text-gray-400 dark:text-gray-500">
                {new Date(d.createdAt).toLocaleString()}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
