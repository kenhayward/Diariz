import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { canSyncOutlook, onOutlookState, outlookAvailable, syncOutlookNow } from "../../lib/outlookSync";
import { CalendarIcon } from "../icons";
import SourceCard, { cardBtn } from "../SourceCard";
import type { OutlookSource } from "../../lib/types";

const DEFAULT_COLOR = "#0F6CBD"; // Outlook blue

/// The desktop Outlook mirror: opt in to copying a classic Outlook calendar from a Windows PC, and manage
/// the machines that have connected one.
///
/// The card is shown to everyone, not only on the desktop - a browser user has to be able to read what
/// this does, see which of their machines are syncing, and revoke it. Only "Sync now" is desktop-gated.
export default function OutlookCard() {
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

  const num = "w-[52px] rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";
  // The count goes in the chip, not on the end of the description: this header carries a chip, a
  // checkbox and a button, so it is the tightest of the three and an appended count was simply cut off.
  const count = sources?.length ?? 0;

  return (
    <SourceCard
      name={t("calendarsOutlookName")}
      meta={t("calendarsOutlookMeta")}
      tint={DEFAULT_COLOR}
      glyph={CalendarIcon}
      // Not "Mirroring": the "Mirror enabled" tick immediately to its right already says that.
      status={count > 0 ? t("calendarsOutlookMachines", { count }) : undefined}
      statusTone="muted"
      error={error}
      actions={
        <>
          <label className="flex shrink-0 items-center gap-1.5 text-xs text-gray-600 dark:text-gray-400">
            <input
              type="checkbox"
              checked={enabled}
              disabled={busy}
              onChange={(e) => toggleOptIn(e.target.checked)}
              aria-label={t("outlookOptIn")}
            />
            {t("calendarsOutlookEnabled")}
          </label>
          {onDesktop && available && enabled && (
            <button type="button" onClick={syncNow} disabled={phase !== "idle"} className={cardBtn}>
              {phase === "idle" ? t("outlookSyncNow") : t("outlookSyncing")}
            </button>
          )}
        </>
      }
    >
      <p className="text-[11px] text-gray-400 dark:text-gray-500">{t("outlookPrivacyNote")}</p>

      {/* A browser (or a Mac) can manage everything here except starting a sync, so say where that happens
          rather than showing a button that could not work. */}
      {!onDesktop && <p className="mt-1.5 text-[11px] text-gray-400 dark:text-gray-500">{t("outlookRequiresDesktop")}</p>}
      {onDesktop && !available && (
        <p className="mt-1.5 text-[11px] text-gray-400 dark:text-gray-500">{t("outlookErrNotAvailable")}</p>
      )}

      <ul className="mt-2 space-y-2">
        {sources?.map((s) => (
          <li key={s.id} className="text-xs">
            <div className="flex items-center gap-2">
              <span
                aria-hidden
                className="inline-block h-2.5 w-2.5 shrink-0 rounded-sm border dark:border-gray-600"
                style={{ backgroundColor: s.color ?? DEFAULT_COLOR }}
              />
              <input
                defaultValue={s.displayName}
                onBlur={(e) => {
                  const name = e.target.value.trim();
                  if (name && name !== s.displayName) void update(s, { displayName: name });
                }}
                aria-label={t("outlookDeviceName")}
                className={`min-w-0 flex-1 rounded border border-transparent bg-transparent px-1 py-0.5 text-[13px] hover:border-gray-300 dark:text-gray-200 dark:hover:border-gray-600 ${
                  s.enabled ? "text-gray-800 dark:text-gray-200" : "text-gray-400 dark:text-gray-500"
                }`}
              />
              <label className="flex shrink-0 items-center gap-1 text-gray-500 dark:text-gray-400">
                {/* "Shown" reads the same on a feed two cards down, so the accessible name says which
                    machine this one hides. */}
                <input
                  type="checkbox"
                  checked={s.enabled}
                  onChange={() => void update(s, { enabled: !s.enabled })}
                  aria-label={t("outlookShownAria", { name: s.displayName })}
                />
                {t("outlookShown")}
              </label>
              <button
                type="button"
                onClick={() => void remove(s)}
                aria-label={t("outlookRemoveAria", { name: s.displayName })}
                className="shrink-0 text-red-600 hover:underline dark:text-red-400"
              >
                {t("outlookRemove")}
              </button>
            </div>

            <p className="ml-4.5 mt-0.5 text-[11px] text-gray-400 dark:text-gray-500">
              {s.mailboxName ?? s.deviceName ?? t("outlookUnknownDevice")}
              {" - "}
              {s.lastSyncedAt
                ? t("outlookSyncedAt", { count: s.eventCount, when: new Date(s.lastSyncedAt).toLocaleString() })
                : t("outlookNeverSynced")}
            </p>
            {s.lastError && (
              <p className="ml-4.5 mt-0.5 text-[11px] text-red-600 dark:text-red-400">
                {t("outlookLastError", { error: s.lastError })}
              </p>
            )}

            <div className="ml-4.5 mt-1 flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-gray-500 dark:text-gray-400">
              <label className="flex items-center gap-1.5">
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
              <label className="flex items-center gap-1.5">
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
              <label className="flex items-center gap-1.5">
                <input
                  type="checkbox"
                  checked={s.skipPrivate}
                  onChange={() => void update(s, { skipPrivate: !s.skipPrivate })}
                  aria-label={t("outlookSkipPrivate")}
                />
                {t("outlookSkipPrivate")}
              </label>
              <label className="flex items-center gap-1.5">
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
    </SourceCard>
  );
}
