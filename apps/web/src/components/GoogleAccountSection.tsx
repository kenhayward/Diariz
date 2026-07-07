import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";

/// Google Account tab: connect/disconnect Google data access + a picker for which Google calendars to
/// consider for recording attribution and the Calendar overlay.
export default function GoogleAccountSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const [gCalendar, setGCalendar] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (profile) setGCalendar(profile.googleCalendar);
  }, [profile]);

  async function connectGoogleData() {
    setError(null);
    try {
      // Full-page navigation to Google's consent screen; it returns to /?google=connected.
      window.location.assign(await api.connectGoogle({ calendar: gCalendar }));
    } catch (e) {
      setError(apiErrorMessage(e, t("googleConnectError")));
    }
  }

  async function disconnectGoogleData() {
    setError(null);
    try {
      await api.disconnectGoogle();
      setGCalendar(false);
      qc.invalidateQueries({ queryKey: ["user-profile"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("googleDisconnectError")));
    }
  }

  return (
    <div className="space-y-4">
      <div>
        <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("googleAccount")}</span>
        {profile?.googleConnected ? (
          <div className="space-y-2 text-sm">
            <p className="text-gray-700 dark:text-gray-200">
              <span className="mr-1.5 inline-block rounded bg-green-100 px-1.5 py-0.5 text-[10px] font-medium text-green-800 dark:bg-green-900/40 dark:text-green-300">
                {t("googleConnectedBadge")}
              </span>
              {t("googleConnectedAs", { email: profile.email })}
            </p>
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={gCalendar} onChange={(e) => setGCalendar(e.target.checked)} />
              <span className="dark:text-gray-200">{t("googleCalendarRead")}</span>
            </label>
            <p className="text-xs text-gray-400 dark:text-gray-500">{t("googleDataHint")}</p>
            <div className="flex gap-2">
              <button
                type="button"
                onClick={connectGoogleData}
                disabled={!gCalendar}
                className="rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
              >
                {t("googleConnectData")}
              </button>
              {profile.googleCalendar && (
                <button
                  type="button"
                  onClick={disconnectGoogleData}
                  className="rounded border px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:border-gray-700 dark:text-red-400 dark:hover:bg-gray-800"
                >
                  {t("googleDisconnect")}
                </button>
              )}
            </div>
          </div>
        ) : (
          <p className="text-sm text-gray-500 dark:text-gray-400">{t("googleNotConnected")}</p>
        )}
        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
      </div>

      {profile?.googleCalendar && <CalendarPicker />}
    </div>
  );
}

/// Checklist of the user's Google calendars (with their colours); pick which count toward attribution.
function CalendarPicker() {
  const { t } = useTranslation("account");
  const { data: calendars, isLoading } = useQuery({
    queryKey: ["google-calendars"],
    queryFn: api.listCalendars,
  });
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (calendars) setSelected(Object.fromEntries(calendars.map((c) => [c.id, c.selected])));
  }, [calendars]);

  async function onSave() {
    setError(null);
    setSaved(false);
    setBusy(true);
    try {
      await api.saveCalendarSelection(Object.entries(selected).filter(([, v]) => v).map(([id]) => id));
      setSaved(true);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <span className="mb-1 block text-sm font-medium text-gray-700 dark:text-gray-200">{t("calendarSelectionTitle")}</span>
      <p className="mb-2 text-xs text-gray-400 dark:text-gray-500">{t("calendarSelectionHint")}</p>
      {isLoading && <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>}
      {calendars && calendars.length === 0 && (
        <p className="text-sm text-gray-500 dark:text-gray-400">{t("calendarSelectionEmpty")}</p>
      )}
      <ul className="space-y-1.5">
        {calendars?.map((c) => (
          <li key={c.id}>
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                aria-label={c.summary ?? c.id}
                checked={selected[c.id] ?? false}
                onChange={(e) => setSelected((s) => ({ ...s, [c.id]: e.target.checked }))}
              />
              <span
                aria-hidden
                className="inline-block h-3 w-3 shrink-0 rounded-full border border-black/10 dark:border-white/20"
                style={{ background: c.backgroundColor ?? "transparent" }}
              />
              <span className="truncate dark:text-gray-200">{c.summary ?? c.id}</span>
              {c.primary && <span className="text-xs text-gray-400 dark:text-gray-500">({t("calendarPrimary")})</span>}
            </label>
          </li>
        ))}
      </ul>
      {calendars && calendars.length > 0 && (
        <div className="mt-3 flex items-center gap-3">
          <button
            type="button"
            onClick={onSave}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("common:saving") : t("common:save")}
          </button>
          {error && <span className="text-sm text-red-600 dark:text-red-400">{error}</span>}
          {saved && !error && <span className="text-sm text-green-600 dark:text-green-400">{t("calendarSelectionSaved")}</span>}
        </div>
      )}
    </div>
  );
}
