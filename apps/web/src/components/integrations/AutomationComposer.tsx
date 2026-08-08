import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { webhookEventGroups } from "../../lib/webhookEvents";
import type { ApiTokenCreated, WebhookSubscription } from "../../lib/types";

/// The create/edit dialog for an automation: which events fire it, where it goes, what it is called.
///
/// This was the Automations tab itself - nine stacked checkboxes that pushed your existing automations
/// below the fold, so the page you visited to check on an automation opened on a form for making another
/// one. The list is the page now, and this is the form.
///
/// The checkboxes became toggle chips in three groups, which is the same nine choices in a third of the
/// height. The grouping lives in `lib/webhookEvents.ts` so the platform picker can adopt it without the
/// two drifting apart.

const CHIP_OFF =
  "rounded-full px-2.5 py-[3px] text-xs bg-white text-gray-500 ring-1 ring-gray-200 hover:bg-gray-50 dark:bg-transparent dark:text-gray-400 dark:ring-gray-700 dark:hover:bg-gray-800";
const CHIP_ON =
  "rounded-full px-2.5 py-[3px] text-xs bg-green-100 text-green-950 ring-1 ring-green-300 dark:bg-green-500/[0.16] dark:text-green-100 dark:ring-green-500/40";

export default function AutomationComposer({
  editing,
  onSaved,
  onClose,
}: {
  /// The automation being edited, or null to create a new one.
  editing: WebhookSubscription | null;
  onSaved: () => void;
  onClose: () => void;
}) {
  const { t } = useTranslation("account");
  const groups = webhookEventGroups(t);

  const [name, setName] = useState(editing?.name ?? "");
  const [url, setUrl] = useState(editing?.url ?? "");
  const [selected, setSelected] = useState<Record<string, boolean>>(
    Object.fromEntries((editing?.eventTypes ?? []).map((k) => [k, true])),
  );
  const [includeContacts, setIncludeContacts] = useState(editing?.includeAttendeeContacts ?? false);
  const [secret, setSecret] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [tokenCreated, setTokenCreated] = useState<ApiTokenCreated | null>(null);
  const [tokenError, setTokenError] = useState<string | null>(null);

  // Flattened in the order the groups present them, which is the canonical event order.
  const eventTypes = groups.flatMap((g) => g.events.map((e) => e.key)).filter((key) => selected[key]);
  const wantsFormulaToken = eventTypes.some((key) => key.startsWith("formula_result"));
  const copy = (text: string) => void navigator.clipboard?.writeText(text);

  async function createToken() {
    setTokenError(null);
    try {
      setTokenCreated(await api.createApiToken(t("automationTokenName"), { readOnly: true, expiresAt: null }));
    } catch (e) {
      setTokenError(apiErrorMessage(e, t("automationCreateError")));
    }
  }

  async function save() {
    setError(null);
    setBusy(true);
    try {
      if (editing) {
        await api.updateWebhook(editing.id, {
          name: name.trim() || t("automationDefaultName"),
          url,
          eventTypes,
          isActive: editing.isActive,
          includeAttendeeContacts: includeContacts,
        });
        onSaved();
        onClose();
      } else {
        const result = await api.createWebhook({
          name: name.trim() || t("automationDefaultName"),
          url,
          eventTypes,
          includeAttendeeContacts: includeContacts,
        });
        onSaved();
        // The signing secret exists here and nowhere else, so the dialog stays up holding it rather than
        // closing and dropping it behind itself.
        setSecret(result.secret);
      }
    } catch (e) {
      setError(apiErrorMessage(e, t("automationCreateError")));
    } finally {
      setBusy(false);
    }
  }

  const title = editing ? t("automationComposerEdit") : t("automationComposerTitle");

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="flex max-h-[85vh] w-full max-w-[560px] flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center justify-between gap-2 border-b px-4 py-3 dark:border-gray-700">
          <h3 className="text-sm font-semibold dark:text-gray-100">{title}</h3>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("common:close")}
            className="rounded px-1 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
          >
            ✕
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          {secret ? (
            <div className="rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
              <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{t("automationSecretOnce")}</p>
              <div className="mt-1 flex items-center gap-2">
                <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">
                  {secret}
                </code>
                <button type="button" onClick={() => copy(secret)} className={btn}>
                  {t("apiCopyToken")}
                </button>
              </div>
            </div>
          ) : (
            <>
              <span className="block text-[13px] text-gray-700 dark:text-gray-200">{t("automationWhenHeading")}</span>
              {groups.map((group) => (
                <div key={group.id} className="mt-2">
                  <span className="mb-1 block text-[10px] uppercase tracking-wide text-gray-400 dark:text-gray-600">
                    {group.label}
                  </span>
                  <div className="flex flex-wrap gap-1.5">
                    {group.events.map((evt) => {
                      const on = selected[evt.key] ?? false;
                      return (
                        <button
                          key={evt.key}
                          type="button"
                          aria-pressed={on}
                          onClick={() => setSelected((s) => ({ ...s, [evt.key]: !on }))}
                          className={on ? CHIP_ON : CHIP_OFF}
                        >
                          {evt.label}
                        </button>
                      );
                    })}
                  </div>
                  {/* A formula caller usually needs to read the result back, so the offer sits with the
                      formula chips rather than at the bottom of the dialog. */}
                  {group.id === "formulas" && wantsFormulaToken && !tokenCreated && (
                    <div className="mt-2 rounded border border-blue-200 bg-blue-50 p-2 dark:border-blue-800/60 dark:bg-blue-900/20">
                      <p className="text-xs text-blue-800 dark:text-blue-300">{t("automationTokenOffer")}</p>
                      <button type="button" onClick={() => void createToken()} className={`mt-1 ${btn}`}>
                        {t("automationTokenCreate")}
                      </button>
                      {tokenError && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{tokenError}</p>}
                    </div>
                  )}
                  {group.id === "formulas" && tokenCreated && (
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
              ))}

              <div className="mt-4 border-t pt-3 dark:border-gray-700">
                <label className="mb-1 block text-[13px] text-gray-700 dark:text-gray-200" htmlFor="automation-url">
                  {t("automationWhereHeading")}
                </label>
                <input
                  id="automation-url"
                  value={url}
                  onChange={(e) => setUrl(e.target.value)}
                  aria-label={t("automationDestinationUrl")}
                  placeholder="https://hooks.zapier.com/..."
                  className={input}
                />
                {/* One sentence, where two toggle tabs used to sit swapping between three. They also nested
                    a second role="tablist" inside the Preferences one, which read badly. */}
                <p className="mt-1 text-[11px] text-gray-400 dark:text-gray-500">{t("automationHint")}</p>

                <input
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  placeholder={t("automationDefaultName")}
                  aria-label={t("automationDefaultName")}
                  className={`mt-2 ${input}`}
                />

                <label className="mt-3 flex items-start gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={includeContacts}
                    onChange={(e) => setIncludeContacts(e.target.checked)}
                    className="mt-0.5"
                  />
                  <span>
                    <span className="text-gray-700 dark:text-gray-200">{t("automationIncludeContacts")}</span>
                    <span className="mt-0.5 block text-xs text-gray-500 dark:text-gray-400">
                      {t("automationIncludeContactsHint")}
                    </span>
                  </span>
                </label>
              </div>
              {error && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
            </>
          )}
        </div>

        <div className="flex items-center justify-between gap-2 border-t px-4 py-3 dark:border-gray-700">
          <span className="text-[11px] text-gray-400 dark:text-gray-500">
            {secret ? "" : t("automationTriggerCount", { count: eventTypes.length })}
          </span>
          <div className="flex gap-2">
            {secret ? (
              <button type="button" onClick={onClose} className={btn}>
                {t("common:close")}
              </button>
            ) : (
              <>
                <button type="button" onClick={onClose} className={btn}>
                  {t("common:cancel")}
                </button>
                <button
                  type="button"
                  onClick={() => void save()}
                  disabled={busy || !url || eventTypes.length === 0}
                  className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
                >
                  {editing ? t("common:save") : t("automationCreate")}
                </button>
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

const btn =
  "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";
const input = "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";
