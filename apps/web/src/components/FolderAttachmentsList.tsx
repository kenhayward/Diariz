import { useState } from "react";
import { useTranslation } from "react-i18next";
import { NavLink } from "react-router-dom";
import { api } from "../lib/api";
import { isMarkdownAttachment } from "../lib/attachments";
import { formatBytes } from "../lib/format";
import { useRoomBasePath } from "../lib/rooms";
import type { SectionAttachmentItem } from "../lib/types";
import { openAttachment } from "./AttachmentsManager";
import MarkdownAttachmentEditModal from "./MarkdownAttachmentEditModal";

/// The folder Attachments tab: every attachment across the folder's recordings (and sub-folders), with a
/// read-only **Meeting** column. Adding is not offered (attachments belong to a recording, not a folder).
/// Open/edit and remove are owner-only for a **File** attachment - viewing its content and mutating it both
/// hit `AttachmentsController` routes gated on the recording's owner - so those are only offered on a row
/// whose source recording belongs to `myUserId`; a **Url** attachment's "Open" needs no API call (the address
/// is data already in the row), so it stays available regardless of ownership, but Remove is still owner-only.
/// Markdown attachments open the in-app editor (saved back through the recording route).
export default function FolderAttachmentsList({
  items,
  myUserId,
  onRemove,
  onChange,
}: {
  items: SectionAttachmentItem[];
  /// The signed-in user's id (`useAuth().id`), compared against each row's `recordedByUserId` - mirrors
  /// RecordingDetail's `isOwner` check, just applied per row since rows here span many recordings.
  myUserId: string | null;
  onRemove: (item: SectionAttachmentItem) => void;
  onChange?: () => void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const [editing, setEditing] = useState<SectionAttachmentItem | null>(null);
  // Keep meeting links inside the room being viewed - see FolderRecordingList for the room-prefix rationale.
  const basePath = useRoomBasePath();
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
          {items.map((a) => {
            const isOwner = myUserId != null && a.recordedByUserId === myUserId;
            // A Url attachment's "Open" is just following a.url - no API call, so it's safe regardless of
            // ownership. Every other route (File content, Markdown text, Remove) is owner-only.
            const canOpen = a.kind === "Url" || isOwner;
            return (
              <tr key={a.id} className="align-top">
                <td className="truncate py-1 pr-2 text-xs">
                  <NavLink to={`${basePath}/recordings/${a.recordingId}`} className="text-blue-600 hover:underline dark:text-blue-400" title={a.recordingName}>
                    {a.recordingName}
                  </NavLink>
                </td>
                <td className="truncate py-1 pr-2" title={a.name}>{a.name}</td>
                <td className="py-1 pr-2 text-xs text-gray-500 dark:text-gray-400">
                  {a.kind === "Url" ? t("workspace:attachmentUrlType") : formatBytes(a.sizeBytes)}
                </td>
                <td className="py-1 pr-2 text-right">
                  {canOpen && (
                    <button
                      type="button"
                      onClick={() => (isMarkdownAttachment(a) ? setEditing(a) : openAttachment(a.recordingId, a))}
                      className="rounded border px-2 py-0.5 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
                    >
                      {isMarkdownAttachment(a) ? t("workspace:editAttachment") : t("workspace:openAttachment")}
                    </button>
                  )}
                  {isOwner && (
                    <button
                      type="button"
                      onClick={() => onRemove(a)}
                      title={t("common:remove")}
                      className="ml-1 rounded px-1 text-gray-400 hover:bg-gray-100 hover:text-red-600 dark:hover:bg-gray-800 dark:hover:text-red-400"
                    >
                      ✕
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>

      {editing && (
        <MarkdownAttachmentEditModal
          name={editing.name}
          load={() => api.getAttachmentText(editing.recordingId, editing.id)}
          save={(md) => api.updateAttachmentContent(editing.recordingId, editing.id, md)}
          onSaved={onChange}
          onClose={() => setEditing(null)}
        />
      )}
    </div>
  );
}
