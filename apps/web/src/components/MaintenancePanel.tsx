import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDuration } from "../lib/format";
import type { BackupStatus, RestoreResult } from "../lib/types";
import { useAuth } from "../auth";

/// How often the panel asks the server how the archive build is going.
const BACKUP_POLL_MS = 1500;
/// How long to keep asking before giving up when the server never reports a build. Covers a click that never
/// produced a request (blocked download, dropped connection) and a tiny platform whose archive was built
/// between two polls - without it the progress line would sit there for ever.
const BACKUP_GRACE_MS = 9000;

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
  const [result, setResult] = useState<RestoreResult | null>(null);
  // Tag backfill (manual one-shot): queue tag extraction for every never-tagged recording.
  const [tagRunBusy, setTagRunBusy] = useState(false);
  const [tagRunMsg, setTagRunMsg] = useState<string | null>(null);
  // Backup: the download is a plain anchor, and the server sends nothing until the whole archive is built -
  // minutes of silence on a large platform. Poll the server's build status so the panel can say it's running.
  const [backupWatching, setBackupWatching] = useState(false);
  const [backupStatus, setBackupStatus] = useState<BackupStatus | null>(null);
  const [backupElapsed, setBackupElapsed] = useState(0);
  const [backupReady, setBackupReady] = useState(false);
  const backupStartedAt = useRef(0);
  const sawBuildRunning = useRef(false);
  // The upload is only the first half of a restore; the server-side work that follows has no progress to
  // report, so it gets its own elapsed timer.
  const [applyElapsed, setApplyElapsed] = useState(0);
  const applying = busy && progress >= 100;

  function watchBackup() {
    backupStartedAt.current = Date.now();
    sawBuildRunning.current = false;
    setBackupStatus(null);
    setBackupElapsed(0);
    setBackupReady(false);
    setBackupWatching(true);
  }

  useEffect(() => {
    if (!backupWatching) return;
    const ticker = setInterval(() => setBackupElapsed(Date.now() - backupStartedAt.current), 1000);
    const poll = setInterval(async () => {
      let status: BackupStatus;
      try {
        status = await api.backupStatus();
      } catch {
        return; // a blip while the server is busy building - keep watching
      }
      setBackupStatus(status);
      if (status.running) {
        sawBuildRunning.current = true;
        return;
      }
      // Idle. If we saw the build running, it has finished and the browser download has taken over; if we
      // never did, stop watching once the grace window is up rather than claiming a backup was made.
      if (sawBuildRunning.current) {
        setBackupWatching(false);
        setBackupReady(true);
      } else if (Date.now() - backupStartedAt.current > BACKUP_GRACE_MS) {
        setBackupWatching(false);
      }
    }, BACKUP_POLL_MS);
    return () => {
      clearInterval(ticker);
      clearInterval(poll);
    };
  }, [backupWatching]);

  useEffect(() => {
    if (!applying) {
      setApplyElapsed(0);
      return;
    }
    const startedAt = Date.now();
    const id = setInterval(() => setApplyElapsed(Date.now() - startedAt), 1000);
    return () => clearInterval(id);
  }, [applying]);

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
      const res = await api.restoreBackup(file, setProgress);
      setResult(res);
      setDone(true);
      // A forward-migrated restore left the schema matching the running code, so the session is still valid -
      // we keep the admin on the page to read the "restart the app" hint. A same-version restore replaced the
      // database + session wholesale, so sign out and reload to a clean state.
      if (!res.restartRecommended) {
        setTimeout(() => {
          logout();
          window.location.reload();
        }, 1500);
      }
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
          onClick={watchBackup}
          className="inline-block rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
        >
          {t("downloadBackup")}
        </a>
        {backupWatching && (
          <div role="status" className="space-y-0.5">
            <p className="text-xs text-gray-600 dark:text-gray-300">
              {backupStatus?.phase === "Objects"
                ? t("backupArchivingFiles", {
                    files: backupStatus.objectsArchived,
                    elapsed: formatDuration(backupElapsed),
                  })
                : t("backupCopyingDatabase", { elapsed: formatDuration(backupElapsed) })}
            </p>
            <p className="text-xs text-gray-500 dark:text-gray-400">{t("backupPreparing")}</p>
          </div>
        )}
        {backupReady && <p className="text-xs text-green-600 dark:text-green-400">{t("backupReady")}</p>}
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
        {busy && (
          <p role="status" className="text-xs text-gray-500 dark:text-gray-400">
            {applying
              ? t("restoreApplying", { elapsed: formatDuration(applyElapsed) })
              : t("restoring", { percent: progress })}
          </p>
        )}
        {done && !result?.restartRecommended && (
          <p className="text-xs text-green-600 dark:text-green-400">{t("restoreSuccess")}</p>
        )}
        {done && result?.restartRecommended && (
          <>
            <p className="text-xs text-green-600 dark:text-green-400">
              {t("restoreMigrated", { from: result.migratedFrom, to: result.migratedTo })}
            </p>
            <p className="rounded bg-amber-50 p-2 text-xs text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
              {t("restoreRestartHint")}
            </p>
          </>
        )}
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
