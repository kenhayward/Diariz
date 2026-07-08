import { useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { useAuth } from "../auth";

/// Platform-Administrator-only Maintenance tab content: download a full backup (Postgres + all object-store
/// blobs) and restore from one. Restore is destructive — it replaces ALL data — so it's gated behind an
/// explicit checkbox and signs the admin out afterwards (their session/user row was replaced).
export default function MaintenancePanel() {
  const { t } = useTranslation("account");
  const { logout } = useAuth();
  const [file, setFile] = useState<File | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  // Tag backfill (manual one-shot): queue tag extraction for every never-tagged recording.
  const [tagRunBusy, setTagRunBusy] = useState(false);
  const [tagRunMsg, setTagRunMsg] = useState<string | null>(null);

  async function runTagBackfillNow() {
    if (!window.confirm(t("runTagBackfillConfirm"))) return;
    setTagRunMsg(null);
    setTagRunBusy(true);
    try {
      const { enqueued } = await api.runTagBackfill();
      setTagRunMsg(t("runTagBackfillResult", { count: enqueued }));
    } catch (e) {
      setTagRunMsg(apiErrorMessage(e));
    } finally {
      setTagRunBusy(false);
    }
  }

  async function restore() {
    if (!file || !confirmed) return;
    setError(null);
    setBusy(true);
    setProgress(0);
    try {
      await api.restoreBackup(file, setProgress);
      setDone(true);
      // The database and session were replaced — sign out and reload to a clean state.
      setTimeout(() => {
        logout();
        window.location.reload();
      }, 1500);
    } catch (e) {
      setError(apiErrorMessage(e, t("restoreFailed")));
      setBusy(false);
    }
  }

  return (
    <div className="space-y-5">
      <section className="space-y-2">
        <h3 className="text-sm font-medium text-gray-700 dark:text-gray-200">{t("backupHeading")}</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400">{t("backupDescription")}</p>
        <p className="rounded bg-amber-50 p-2 text-xs text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
          {t("backupSensitive")}
        </p>
        <a
          href={api.backupUrl()}
          className="inline-block rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
        >
          {t("downloadBackup")}
        </a>
      </section>

      <section className="space-y-2 border-t pt-4 dark:border-gray-700">
        <h3 className="text-sm font-medium text-gray-700 dark:text-gray-200">{t("tagBackfillHeading")}</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400">{t("tagBackfillDescription")}</p>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={runTagBackfillNow}
            disabled={tagRunBusy}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {tagRunBusy ? t("runTagBackfillRunning") : t("runTagBackfillNow")}
          </button>
          {tagRunMsg && <span className="text-xs text-gray-600 dark:text-gray-300">{tagRunMsg}</span>}
        </div>
      </section>

      <section className="space-y-2 border-t pt-4 dark:border-gray-700">
        <h3 className="text-sm font-medium text-red-700 dark:text-red-400">{t("restoreHeading")}</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400">{t("restoreDescription")}</p>
        <p className="rounded bg-red-50 p-2 text-xs text-red-700 dark:bg-red-900/30 dark:text-red-300">
          {t("restoreDanger")}
        </p>
        <input
          type="file"
          accept=".zip,application/zip"
          aria-label={t("chooseBackupFile")}
          disabled={busy}
          onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          className="block w-full text-sm text-gray-600 file:mr-3 file:rounded file:border file:bg-gray-50 file:px-2 file:py-1 file:text-sm dark:text-gray-300 dark:file:border-gray-700 dark:file:bg-gray-800 dark:file:text-gray-200"
        />
        <label className="flex items-start gap-2 text-xs text-gray-600 dark:text-gray-300">
          <input
            type="checkbox"
            className="mt-0.5"
            checked={confirmed}
            disabled={busy}
            onChange={(e) => setConfirmed(e.target.checked)}
          />
          <span>{t("restoreConfirm")}</span>
        </label>
        {busy && <p className="text-xs text-gray-500 dark:text-gray-400">{t("restoring", { percent: progress })}</p>}
        {done && <p className="text-xs text-green-600 dark:text-green-400">{t("restoreSuccess")}</p>}
        {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}
        <button
          type="button"
          onClick={restore}
          disabled={!file || !confirmed || busy}
          className="rounded bg-red-600 px-3 py-1.5 text-sm text-white hover:bg-red-700 disabled:opacity-50"
        >
          {t("restoreButton")}
        </button>
      </section>
    </div>
  );
}
