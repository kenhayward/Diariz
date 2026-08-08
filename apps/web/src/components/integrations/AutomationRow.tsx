import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../../lib/api";
import KebabMenu from "../KebabMenu";
import type { WebhookSubscription } from "../../lib/types";

/// One automation, on one line: a status dot, its name over a meta line, a status pill, and a kebab.
///
/// It used to be a card with four always-visible buttons and a chip per trigger, which made a list of
/// three automations taller than the screen. The triggers are in the meta line as text now - resist
/// putting the chips back, that is the whole change.
export default function AutomationRow({
  hook,
  eventLabel,
  onEdit,
  onSendTest,
  onSetActive,
  onRemove,
}: {
  hook: WebhookSubscription;
  eventLabel: (key: string) => string;
  onEdit: (hook: WebhookSubscription) => void;
  onSendTest: (hook: WebhookSubscription) => void;
  onSetActive: (hook: WebhookSubscription, isActive: boolean) => void;
  onRemove: (hook: WebhookSubscription) => void;
}) {
  const { t } = useTranslation("account");
  const paused = !hook.isActive;
  // The server sets a reason only when it gave up on the endpoint itself. Without one, the user paused it
  // deliberately, and telling them to go and check the URL would be nonsense.
  const autoPaused = paused && hook.disabledReason != null;
  const [expanded, setExpanded] = useState(false);

  const { data: deliveries, isLoading } = useQuery({
    queryKey: ["webhook-deliveries", hook.id],
    queryFn: () => api.listWebhookDeliveries(hook.id),
    enabled: expanded,
  });

  const host = (() => {
    try {
      return new URL(hook.url).host;
    } catch {
      return hook.url;
    }
  })();

  // What it fires on and where it goes, as text. A paused automation says why instead of when it last
  // delivered, because that is the thing the reader needs.
  const meta = [
    host,
    hook.eventTypes.map(eventLabel).join(", "),
    paused
      ? (hook.disabledReason ?? hook.lastStatus ?? null)
      : hook.lastDeliveryAt
        ? t("automationLastDelivered", { when: new Date(hook.lastDeliveryAt).toLocaleString() })
        : null,
  ].filter(Boolean).join(" · ");

  return (
    <li className="border-b border-gray-100 py-2 last:border-b-0 dark:border-gray-800">
      <div className="flex items-center gap-2.5">
        <span
          aria-hidden
          className={`h-[7px] w-[7px] shrink-0 rounded-full ${paused ? "bg-[var(--hub-red)]" : "bg-green-500"}`}
        />
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] text-gray-800 dark:text-gray-200">{hook.name}</div>
          <div className={`truncate text-[11px] ${autoPaused ? "text-red-600 dark:text-red-400" : "text-gray-400 dark:text-gray-500"}`}>
            {meta}
          </div>
        </div>
        <span
          className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-medium ${
            paused
              ? "bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300"
              : "bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-300"
          }`}
        >
          {!paused ? t("automationActive") : autoPaused ? t("automationPaused") : t("automationPausedManual")}
        </span>
        {/* Named after the automation: three cards share this page and "Actions" alone would say nothing
            about which one. */}
        <KebabMenu
          label={t("automationActionsAria", { name: hook.name })}
          actions={[
            { label: t("automationEdit"), onClick: () => onEdit(hook) },
            { label: t("automationSendTest"), onClick: () => onSendTest(hook) },
            { label: paused ? t("automationResume") : t("automationPause"), onClick: () => onSetActive(hook, paused) },
            { label: t("automationDeliveries"), onClick: () => setExpanded((e) => !e) },
            { label: t("automationDelete"), onClick: () => onRemove(hook), danger: true },
          ]}
        />
      </div>

      {/* Never fetched until asked for: a page with ten automations would otherwise open ten delivery
          queries nobody looked at. */}
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
    </li>
  );
}
