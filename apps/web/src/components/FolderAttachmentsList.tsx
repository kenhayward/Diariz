import { useTranslation } from "react-i18next";
import { NavLink } from "react-router-dom";
import { formatBytes } from "../lib/format";
import type { SectionAttachmentItem } from "../lib/types";
import { openAttachment } from "./AttachmentsManager";

/// The folder Attachments tab: every attachment across the folder's recordings (and sub-folders), with a
/// read-only **Meeting** column. Open + remove are allowed; adding is not (attachments belong to a recording).
export default function FolderAttachmentsList({
  items,
  onRemove,
}: {
  items: SectionAttachmentItem[];
  onRemove: (item: SectionAttachmentItem) => void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  if (items.length === 0)
    return <p className="px-4 pb-4 text-sm text-gray-500 dark:text-gray-400">{t("workspace:folderNoAttachments")}</p>;

  return (
    <div className="px-4 pb-4">
      <table className="w-full table-fixed text-sm">
        <thead>
          <tr className="text-left text-xs font-medium text-gray-400 dark:text-gray-500">
            <th className="w-[22%] pb-1 pr-2 font-medium">{t("workspace:colMeeting")}</th>
            <th className="w-[45%] pb-1 pr-2 font-medium">{t("workspace:attachmentName")}</th>
            <th className="w-[13%] pb-1 pr-2 font-medium">{t("workspace:attachmentSize")}</th>
            <th className="w-[20%] pb-1 pr-2" aria-hidden />
          </tr>
        </thead>
        <tbody>
          {items.map((a) => (
            <tr key={a.id} className="align-top">
              <td className="truncate py-1 pr-2 text-xs">
                <NavLink to={`/recordings/${a.recordingId}`} className="text-blue-600 hover:underline dark:text-blue-400" title={a.recordingName}>
                  {a.recordingName}
                </NavLink>
              </td>
              <td className="truncate py-1 pr-2" title={a.name}>{a.name}</td>
              <td className="py-1 pr-2 text-xs text-gray-500 dark:text-gray-400">
                {a.kind === "Url" ? t("workspace:attachmentUrlType") : formatBytes(a.sizeBytes)}
              </td>
              <td className="py-1 pr-2 text-right">
                <button
                  type="button"
                  onClick={() => openAttachment(a.recordingId, a)}
                  className="rounded border px-2 py-0.5 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                >
                  {t("workspace:openAttachment")}
                </button>
                <button
                  type="button"
                  onClick={() => onRemove(a)}
                  title={t("common:remove")}
                  className="ml-1 rounded px-1 text-gray-400 hover:bg-gray-100 hover:text-red-600 dark:hover:bg-gray-800 dark:hover:text-red-400"
                >
                  ✕
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
