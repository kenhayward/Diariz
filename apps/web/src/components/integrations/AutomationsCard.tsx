import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { webhookEvents } from "../../lib/webhookEvents";
import { RefreshIcon } from "../icons";
import HelpButton from "../HelpButton";
import SourceCard, { cardBtn } from "../SourceCard";
import AutomationRow from "./AutomationRow";
import AutomationComposer from "./AutomationComposer";
import type { WebhookSubscription } from "../../lib/types";

const TINT = "#f59e0b";
const TINT_DARK = "#fcd34d";

/// Outbound automations: meeting events pushed to Zapier, n8n, Make or any webhook.
///
/// List-first. The tab used to open on the create form, so the automations you came to check on were
/// below the fold; making a new one is a dialog now and the body is simply what you have.
export default function AutomationsCard() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const enabled = profile?.webhooksEnabled === true;

  const { data: webhooks } = useQuery({ queryKey: ["webhooks"], queryFn: api.listWebhooks, enabled });

  const [composing, setComposing] = useState(false);
  const [editing, setEditing] = useState<WebhookSubscription | null>(null);
  const [error, setError] = useState<string | null>(null);

  const EVENTS = webhookEvents(t);
  const eventLabel = (key: string) => EVENTS.find((e) => e.key === key)?.label ?? key;
  const refresh = () => qc.invalidateQueries({ queryKey: ["webhooks"] });

  async function sendTest(hook: WebhookSubscription) {
    setError(null);
    try {
      await api.testWebhook(hook.id);
    } catch (e) {
      setError(apiErrorMessage(e, t("automationTestError")));
    }
  }

  async function remove(hook: WebhookSubscription) {
    if (!window.confirm(t("automationDeleteConfirm", { name: hook.name }))) return;
    setError(null);
    try {
      await api.deleteWebhook(hook.id);
      refresh();
    } catch (e) {
      setError(apiErrorMessage(e, t("automationDeleteError")));
    }
  }

  /// Pause or resume without deleting. The endpoint replaces rather than patches, so the whole record goes
  /// back - dropping includeAttendeeContacts here would silently stop contact details being sent. The
  /// server clears the failure count when something is re-activated, which is also how an auto-paused
  /// automation recovers, so both halves of the toggle go through this one call.
  ///
  /// `signalFilter` is deliberately absent: it is not on `UpdateWebhookRequest` at all (only the platform
  /// variant carries one), so the personal endpoint cannot touch it and this cannot clear it.
  async function setActive(hook: WebhookSubscription, isActive: boolean) {
    setError(null);
    try {
      await api.updateWebhook(hook.id, {
        name: hook.name,
        url: hook.url,
        eventTypes: hook.eventTypes,
        isActive,
        includeAttendeeContacts: hook.includeAttendeeContacts,
      });
      refresh();
    } catch (e) {
      setError(apiErrorMessage(e, t("automationPauseError")));
    }
  }

  // The count goes in the chip, not on the end of the meta line: appended, it was the first thing to be
  // truncated away, and a chip is what the other two cards use to say how much of a thing there is.
  const count = webhooks?.length ?? 0;

  return (
    <>
      <SourceCard
        name={t("integrationsAutomationsName")}
        meta={t("integrationsAutomationsMeta")}
        tint={TINT}
        tintDark={TINT_DARK}
        glyph={RefreshIcon}
        status={count > 0 ? t("integrationsAutomationCount", { count }) : undefined}
        statusTone="muted"
        help={<HelpButton topic="automations-and-signals" className="ml-1" />}
        error={error}
        disabledNote={profile != null && !enabled ? t("integrationsAutomationsDisabled") : null}
        actions={
          <button
            type="button"
            onClick={() => {
              setEditing(null);
              setComposing(true);
            }}
            className={cardBtn}
          >
            {t("integrationsNewAutomation")}
          </button>
        }
      >
        <ul>
          {webhooks?.map((hook) => (
            <AutomationRow
              key={hook.id}
              hook={hook}
              eventLabel={eventLabel}
              onEdit={(h) => {
                setEditing(h);
                setComposing(true);
              }}
              onSendTest={(h) => void sendTest(h)}
              onSetActive={(h, active) => void setActive(h, active)}
              onRemove={(h) => void remove(h)}
            />
          ))}
          {webhooks?.length === 0 && (
            <li className="text-xs text-gray-400 dark:text-gray-500">{t("automationEmpty")}</li>
          )}
        </ul>
      </SourceCard>

      {composing && (
        <AutomationComposer
          editing={editing}
          onSaved={refresh}
          onClose={() => {
            setComposing(false);
            setEditing(null);
          }}
        />
      )}
    </>
  );
}
