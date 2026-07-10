import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { useStatus } from "../lib/status";
import { useUpload } from "../lib/uploadContext";
import { pipelineStatus, toneClass, type StatusTone } from "../lib/statusBar";
import { formatBytes, storagePercent } from "../lib/format";
import { transcriptionTimeParts } from "../lib/transcriptionTime";

/// App-wide status bar locked to the bottom of the page (a shrink-0 flex child of the full-height shell, so
/// it never scrolls). Left: the current progress message in its tone colour — an explicitly-pushed status
/// (uploads, chat, the recording page's client-only actions) wins, else in-flight uploads, else a pipeline
/// message derived from the recordings list. Right: `|`-delimited storage usage, transcription usage, and
/// total transcripts (as shown in the account menu).
export default function StatusBar() {
  const { t, i18n } = useTranslation(["account", "workspace"]);
  const { status } = useStatus();
  const { busy, items } = useUpload();
  const { data: recordings = [] } = useQuery({ queryKey: ["recordings"], queryFn: api.listRecordings });
  const { data: storage } = useQuery({ queryKey: ["user-storage"], queryFn: api.getUserStorage });

  // Left message: explicit push > uploads in flight > derived pipeline > nothing.
  let message: { text: string; tone: StatusTone } | null = status;
  if (!message && busy) {
    const n = items.filter((i) => i.status === "uploading" || i.status === "queued").length;
    message = { text: t("workspace:statusUploading", { count: n }), tone: "progress" };
  }
  if (!message) {
    const pipeline = pipelineStatus(recordings);
    if (pipeline) message = { text: t(`workspace:${pipeline.key}`), tone: pipeline.tone };
  }

  const fields: string[] = [];
  if (storage) {
    fields.push(
      t("account:storageUsage", {
        used: formatBytes(storage.usedBytes),
        total: formatBytes(storage.quotaBytes),
        percent: storagePercent(storage.usedBytes, storage.quotaBytes),
      }),
    );
    const { days, clock } = transcriptionTimeParts(storage.totalTranscriptionMs);
    fields.push(days > 0 ? t("account:transcriptionTotalDays", { days, clock }) : t("account:transcriptionTotal", { clock }));
  }
  fields.push(t("account:transcriptsTotal", { n: recordings.length.toLocaleString(i18n.language) }));

  return (
    <footer className="flex h-5 shrink-0 items-center gap-3 border-t bg-white px-3 text-xs dark:border-gray-700 dark:bg-gray-900">
      {/* Announced: recorder warnings/hints (e.g. "no sound detected") land here rather than inline. */}
      <span
        role="status"
        aria-live="polite"
        className={`min-w-0 flex-1 truncate ${message ? toneClass(message.tone) : ""}`}
      >
        {message?.text ?? ""}
      </span>
      <span className="shrink-0 text-gray-500 dark:text-gray-400">{fields.join("  |  ")}</span>
    </footer>
  );
}
