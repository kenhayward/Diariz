import { useMemo, useState, useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatDate } from "../lib/format";

/// Pick one of the user's recordings to link to a calendar event (the inverse of the recording-side link).
/// Used from the recording-less event preview. On success calls `onLinked(recordingId)`.
export default function LinkRecordingModal({
  eventId,
  calendarId,
  onClose,
  onLinked,
}: {
  eventId: string;
  calendarId?: string | null;
  onClose: () => void;
  onLinked: (recordingId: string) => void;
}) {
  const { t, i18n } = useTranslation(["workspace", "common"]);
  const qc = useQueryClient();
  const [filter, setFilter] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const { data: recordings = [] } = useQuery({ queryKey: ["recordings"], queryFn: api.listRecordings });

  const shown = useMemo(() => {
    const q = filter.trim().toLowerCase();
    return recordings.filter((r) => !q || (r.name ?? r.title).toLowerCase().includes(q));
  }, [recordings, filter]);

  async function link(recordingId: string) {
    setBusy(true);
    setError(null);
    try {
      await api.putCalendarLink(recordingId, eventId, true, calendarId);
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["recording", recordingId] });
      onLinked(recordingId);
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("calPickRecordingTitle")}
        className="flex max-h-[80vh] w-full max-w-md flex-col rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-base font-semibold dark:text-gray-100">{t("calPickRecordingTitle")}</h2>

        <input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder={t("calPickRecordingFilter")}
          aria-label={t("calPickRecordingFilter")}
          className="mt-3 w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />

        <div className="mt-2 min-h-0 flex-1 overflow-y-auto">
          {shown.length === 0 ? (
            <p className="px-1 py-4 text-sm text-gray-500 dark:text-gray-400">{t("calPickNoRecordings")}</p>
          ) : (
            <ul className="space-y-1">
              {shown.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => link(r.id)}
                    className="block w-full rounded border px-3 py-1.5 text-left text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:hover:bg-gray-800"
                  >
                    <div className="truncate text-gray-800 dark:text-gray-200">{r.name ?? r.title}</div>
                    <div className="text-xs tabular-nums text-gray-500 dark:text-gray-400">
                      {formatDate(r.createdAt, i18n.language)}
                    </div>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>

        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="mt-3 flex justify-end border-t pt-3 dark:border-gray-700">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
        </div>
      </div>
    </div>
  );
}
