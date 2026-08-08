import { useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { CalendarIcon } from "../icons";
import SourceCard, { cardBtn, cardBtnDanger } from "../SourceCard";

const TINT = "#1a73e8"; // Google blue

/// The Google Calendar source: the connection itself, and which of the account's calendars count when
/// matching recordings to meetings.
///
/// There is no longer a "Read my Google Calendar" tick. It was the scope asked for when *connecting*, not
/// a setting - it disabled itself once granted and did nothing afterwards, which reads as a broken
/// control. Connection state is the chip and the two buttons; the scope is what Reconnect asks for.
export default function GoogleCalendarCard() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const [error, setError] = useState<string | null>(null);

  const connected = profile?.googleConnected === true;
  const hasCalendar = profile?.googleCalendar === true;

  // Without the scope this 403s, so don't ask - the body offers to grant it instead.
  const { data: calendars } = useQuery({
    queryKey: ["google-calendars"],
    queryFn: api.listCalendars,
    enabled: hasCalendar,
  });

  const [selected, setSelected] = useState<Record<string, boolean>>({});
  useEffect(() => {
    if (calendars) setSelected(Object.fromEntries(calendars.map((c) => [c.id, c.selected])));
  }, [calendars]);

  // Each tick sends the whole selection, so the writes are chained rather than fired in parallel: two
  // rapid ticks in flight at once can land in either order, and the loser would silently win.
  const pending = useRef<Promise<unknown>>(Promise.resolve());

  function toggle(id: string, on: boolean) {
    const next = { ...selected, [id]: on };
    setSelected(next);
    const ids = Object.entries(next).filter(([, v]) => v).map(([calendarId]) => calendarId);
    pending.current = pending.current
      .catch(() => {})
      .then(() => api.saveCalendarSelection(ids))
      .then(
        () => setError(null),
        (e) => setError(apiErrorMessage(e)),
      );
  }

  /// Starts Google's consent flow, always asking for calendar access - the only reason to come back here.
  /// A full-page navigation; Google returns to /?google=connected.
  async function connect() {
    setError(null);
    try {
      window.location.assign(await api.connectGoogle({ calendar: true }));
    } catch (e) {
      setError(apiErrorMessage(e, t("googleConnectError")));
    }
  }

  async function disconnect() {
    if (!window.confirm(t("calendarsGoogleDisconnectConfirm"))) return;
    setError(null);
    try {
      await api.disconnectGoogle();
      qc.invalidateQueries({ queryKey: ["user-profile"] });
      qc.removeQueries({ queryKey: ["google-calendars"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("googleDisconnectError")));
    }
  }

  const inUse = Object.values(selected).filter(Boolean).length;
  const meta = !connected
    ? t("calendarsGoogleMetaEmpty")
    : calendars
      ? t("calendarsGoogleMeta", { email: profile?.email, total: calendars.length, selected: inUse })
      : (profile?.email ?? t("calendarsGoogleMetaEmpty"));

  return (
    <SourceCard
      name={t("calendarsGoogleName")}
      meta={meta}
      tint={TINT}
      glyph={CalendarIcon}
      status={connected ? t("googleConnectedBadge") : undefined}
      error={error}
      actions={
        connected ? (
          <>
            <button type="button" onClick={connect} className={cardBtn}>
              {hasCalendar ? t("calendarsGoogleReconnect") : t("calendarsGoogleGrant")}
            </button>
            {/* Offered whenever Google is connected. It used to appear only once calendar access had been
                granted, which left anyone connected without it unable to disconnect at all. */}
            <button type="button" onClick={disconnect} className={cardBtnDanger}>
              {t("googleDisconnect")}
            </button>
          </>
        ) : undefined
      }
    >
      {!connected ? (
        <p className="text-[13px] text-gray-500 dark:text-gray-400">{t("googleNotConnected")}</p>
      ) : !hasCalendar ? (
        <p className="text-[13px] text-gray-500 dark:text-gray-400">{t("calendarsGoogleGrantPrompt")}</p>
      ) : (
        <>
          <p className="mb-2 text-[11px] text-gray-400 dark:text-gray-500">{t("calendarSelectionHint")}</p>
          {calendars && calendars.length === 0 && (
            <p className="text-[13px] text-gray-500 dark:text-gray-400">{t("calendarSelectionEmpty")}</p>
          )}
          {/* Two columns at the pane's usual width; one when the modal is squeezed. */}
          <div className="grid grid-cols-1 gap-x-5 gap-y-1 sm:grid-cols-2">
            {calendars?.map((c) => (
              <label key={c.id} className="flex items-center gap-2 py-0.5 text-[13px]">
                <input
                  type="checkbox"
                  aria-label={c.summary ?? c.id}
                  checked={selected[c.id] ?? false}
                  onChange={(e) => toggle(c.id, e.target.checked)}
                  className="shrink-0"
                />
                <span
                  aria-hidden
                  className="inline-block h-2.5 w-2.5 shrink-0 rounded-full border border-black/10 dark:border-white/20"
                  style={{ background: c.backgroundColor ?? "transparent" }}
                />
                <span className="min-w-0 truncate dark:text-gray-200">{c.summary ?? c.id}</span>
                {c.primary && (
                  <span className="shrink-0 text-[11px] text-gray-400 dark:text-gray-500">({t("calendarPrimary")})</span>
                )}
              </label>
            ))}
          </div>
        </>
      )}
    </SourceCard>
  );
}
