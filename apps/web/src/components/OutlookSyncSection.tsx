import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { canSyncOutlook, onOutlookState, outlookAvailable, syncOutlookNow } from "../lib/outlookSync";
import type { OutlookSource } from "../lib/types";

const DEFAULT_COLOR = "#0F6CBD"; // Outlook blue

/// The Preferences "Outlook" section: opt in to mirroring a classic desktop Outlook calendar, and manage the
/// machines that have connected one.
///
/// The tab is shown to everyone, not only on the desktop - a browser user has to be able to read what this
/// does, see which of their machines are syncing, and revoke it. Only the "Sync now" button is desktop-gated.
export default function OutlookSyncSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  const { data: sources } = useQuery({ queryKey: ["outlook-sources"], queryFn: api.listOutlookSources });

  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [available, setAvailable] = useState(false);
  const [phase, setPhase] = useState<"idle" | "reading" | "pushing">("idle");

  const enabled = settings?.outlookSyncEnabled === true;
  const onDesktop = canSyncOutlook();

  // Ask the shell once whether it can actually reach Outlook (installed, classic rather than new Outlook).
  useEffect(() => {
    let live = true;
    void outlookAvailable().then((ok) => {
      if (live) setAvailable(ok);
    });
    return () => {
      live = false;
    };
  }, []);

  // Mirror the shell's progress so the button can disable itself while a sync is under way.
  useEffect(() => onOutlookState((s) => setPhase(s.phase)), []);

  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ["outlook-sources"] });
    void qc.invalidateQueries({ queryKey: ["user-settings"] });
  };

  async function toggleOptIn(next: boolean) {
    // Turning it off erases every mirrored meeting, so say that before doing it rather than after.
    if (!next && !window.confirm(t("outlookOptOutConfirm"))) return;
    setError(null);
    setBusy(true);
    try {
      await api.updateUserSettings({ outlookSyncEnabled: next });
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("outlookSaveError")));
    } finally {
      setBusy(false);
    }
  }

  async function update(source: OutlookSource, patch: Parameters<typeof api.updateOutlookSource>[1]) {
    setError(null);
    try {
      await api.updateOutlookSource(source.id, patch);
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("outlookSaveError")));
    }
  }

  async function remove(source: OutlookSource) {
    if (!window.confirm(t("outlookRemoveConfirm", { name: source.displayName }))) return;
    setError(null);
    try {
      await api.deleteOutlookSource(source.id);
      invalidate();
    } catch (e) {
      setError(apiErrorMessage(e, t("outlookRemoveError")));
    }
  }

  async function syncNow() {
    setError(null);
    const { started, reason } = await syncOutlookNow();
    if (!started) setError(t(reason === "cooldown" ? "outlookErrCooldown" : "outlookErrGeneric"));
  }

  const btn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";
  const num =
    "w-16 rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("outlookTitle")}</span>
      <p className="text-xs text-gray-400 dark:text-gray-500">{t("outlookIntro")}</p>

      <label className="mt-2 flex items-start gap-2 text-xs text-gray-700 dark:text-gray-200">
        <input
          type="checkbox"
          checked={enabled}
          disabled={busy}
          onChange={(e) => toggleOptIn(e.target.checked)}
          className="mt-0.5"
        />
        <span>
          {t("outlookOptIn")}
          <span className="mt-0.5 block text-gray-400 dark:text-gray-500">{t("outlookPrivacyNote")}</span>
        </span>
      </label>

      {/* A browser (or a Mac) can manage everything here except starting a sync, so say where that happens
          rather than showing a button that could not work. */}
      {!onDesktop && <p className="mt-2 text-xs text-gray-400 dark:text-gray-500">{t("outlookRequiresDesktop")}</p>}
      {onDesktop && !available && (
        <p className="mt-2 text-xs text-gray-400 dark:text-gray-500">{t("outlookErrNotAvailable")}</p>
      )}
      {onDesktop && available && enabled && (
        <button type="button" onClick={syncNow} disabled={phase !== "idle"} className={`${btn} mt-2`}>
          {phase === "idle" ? t("outlookSyncNow") : t("outlookSyncing")}
        </button>
      )}

      <ul className="mt-3 space-y-2">
        {sources?.map((s) => (
          <li key={s.id} className="text-xs">
            <div className="flex items-center gap-2">
              <span
                aria-hidden
                className="inline-block h-3 w-3 shrink-0 rounded-sm border dark:border-gray-600"
                style={{ backgroundColor: s.color ?? DEFAULT_COLOR }}
              />
              <input
                defaultValue={s.displayName}
                onBlur={(e) => {
                  const name = e.target.value.trim();
                  if (name && name !== s.displayName) void update(s, { displayName: name });
                }}
                aria-label={t("outlookDeviceName")}
                className={`min-w-0 flex-1 rounded border border-transparent bg-transparent px-1 py-0.5 hover:border-gray-300 dark:text-gray-200 dark:hover:border-gray-600 ${
                  s.enabled ? "text-gray-700 dark:text-gray-200" : "text-gray-400 dark:text-gray-500"
                }`}
              />
              <label className="flex shrink-0 items-center gap-1 text-gray-500 dark:text-gray-400">
                <input
                  type="checkbox"
                  checked={s.enabled}
                  onChange={() => void update(s, { enabled: !s.enabled })}
                  aria-label={t("outlookShown")}
                />
                {t("outlookShown")}
              </label>
              <button
                type="button"
                onClick={() => void remove(s)}
                className="shrink-0 text-red-600 hover:underline dark:text-red-400"
              >
                {t("outlookRemove")}
              </button>
            </div>

            <p className="ml-5 mt-0.5 text-[11px] text-gray-400 dark:text-gray-500">
              {s.mailboxName ?? s.deviceName ?? t("outlookUnknownDevice")}
              {" - "}
              {s.lastSyncedAt
                ? t("outlookSyncedAt", { count: s.eventCount, when: new Date(s.lastSyncedAt).toLocaleString() })
                : t("outlookNeverSynced")}
            </p>
            {s.lastError && (
              <p className="ml-5 mt-0.5 text-[11px] text-red-600 dark:text-red-400">
                {t("outlookLastError", { error: s.lastError })}
              </p>
            )}

            <div className="ml-5 mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px] text-gray-500 dark:text-gray-400">
              <label className="flex items-center gap-1">
                {t("outlookWindowPast")}
                <input
                  type="number"
                  min={0}
                  max={365}
                  defaultValue={s.pastDays}
                  onBlur={(e) => {
                    const v = Number(e.target.value);
                    if (Number.isFinite(v) && v !== s.pastDays) void update(s, { pastDays: v });
                  }}
                  aria-label={t("outlookWindowPast")}
                  className={num}
                />
              </label>
              <label className="flex items-center gap-1">
                {t("outlookWindowFuture")}
                <input
                  type="number"
                  min={1}
                  max={730}
                  defaultValue={s.futureDays}
                  onBlur={(e) => {
                    const v = Number(e.target.value);
                    if (Number.isFinite(v) && v !== s.futureDays) void update(s, { futureDays: v });
                  }}
                  aria-label={t("outlookWindowFuture")}
                  className={num}
                />
              </label>
              <label className="flex items-center gap-1">
                <input
                  type="checkbox"
                  checked={s.skipPrivate}
                  onChange={() => void update(s, { skipPrivate: !s.skipPrivate })}
                  aria-label={t("outlookSkipPrivate")}
                />
                {t("outlookSkipPrivate")}
              </label>
              <label className="flex items-center gap-1">
                <input
                  type="checkbox"
                  checked={s.includeBody}
                  onChange={() => void update(s, { includeBody: !s.includeBody })}
                  aria-label={t("outlookIncludeBody")}
                />
                {t("outlookIncludeBody")}
              </label>
            </div>
          </li>
        ))}
        {sources?.length === 0 && (
          <li className="text-xs text-gray-400 dark:text-gray-500">{t("outlookNoDevices")}</li>
        )}
      </ul>

      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
    </div>
  );
}
