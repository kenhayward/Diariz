import { useTranslation } from "react-i18next";
import type { UploadItem } from "../../lib/uploadQueue";

/// Per-file status for the current upload batch (queued/uploading/done/failed). Tolerant of partial
/// failures — a rejected file shows its reason and the rest still upload.
function UploadStatusList({ items, onClear }: { items: UploadItem[]; onClear: () => void }) {
  const { t } = useTranslation("workspace");
  if (items.length === 0) return null;
  const settled = items.every((i) => i.status === "done" || i.status === "failed");
  const tag: Record<UploadItem["status"], string> = {
    queued: "text-gray-400",
    uploading: "text-amber-600 dark:text-amber-400",
    done: "text-green-600 dark:text-green-400",
    failed: "text-red-600 dark:text-red-400",
  };
  const label: Record<UploadItem["status"], string> = {
    queued: t("uploadQueued"),
    uploading: t("uploadUploading"),
    done: t("uploadDone"),
    failed: t("uploadFailed"),
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
            <span className={`shrink-0 ${tag[i.status]}`} title={i.error}>{label[i.status]}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
export default UploadStatusList;
