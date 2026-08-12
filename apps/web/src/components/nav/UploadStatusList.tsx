import { useTranslation } from "react-i18next";
import type { UploadItem } from "../../lib/uploadQueue";

/// Per-file status for the current upload batch. Tolerant of partial failures - a rejected file shows
/// its reason and the rest still upload. A video is extracted before it uploads, which on a long
/// recording takes minutes, so that phase shows progress and can be cancelled.
function UploadStatusList({
  items,
  onClear,
  onCancel,
}: {
  items: UploadItem[];
  onClear: () => void;
  onCancel: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  if (items.length === 0) return null;
  const isSettled = (s: UploadItem["status"]) => s === "done" || s === "failed" || s === "cancelled";
  const settled = items.every((i) => isSettled(i.status));
  const tag: Record<UploadItem["status"], string> = {
    queued: "text-gray-400",
    extracting: "text-amber-600 dark:text-amber-400",
    uploading: "text-amber-600 dark:text-amber-400",
    done: "text-green-600 dark:text-green-400",
    failed: "text-red-600 dark:text-red-400",
    cancelled: "text-gray-400",
  };
  const label: Record<UploadItem["status"], string> = {
    queued: t("uploadQueued"),
    extracting: t("uploadExtracting"),
    uploading: t("uploadUploading"),
    done: t("uploadDone"),
    failed: t("uploadFailed"),
    cancelled: t("uploadCancelled"),
  };
  return (
    <div className="border-b px-3 py-2 dark:border-gray-800">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400">{t("uploadsHeader")}</span>
        {settled && (
          <button type="button" onClick={onClear} className="text-xs text-gray-400 hover:underline">
            {t("clear")}
          </button>
        )}
      </div>
      <ul className="space-y-0.5">
        {items.map((i) => (
          <li key={i.id} className="flex items-center justify-between gap-2 text-xs">
            <span className="truncate dark:text-gray-300" title={i.name}>{i.name}</span>
            <span className="flex shrink-0 items-center gap-1.5">
              <span className={tag[i.status]} title={i.error}>
                {label[i.status]}
                {i.status === "extracting" && i.progress !== undefined
                  ? ` ${Math.round(i.progress * 100)}%`
                  : ""}
              </span>
              {!isSettled(i.status) && (
                <button
                  type="button"
                  onClick={() => onCancel(i.id)}
                  className="text-gray-400 hover:underline"
                >
                  {t("uploadCancel")}
                </button>
              )}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
export default UploadStatusList;
