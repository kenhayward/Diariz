import { useTranslation } from "react-i18next";
import { ATTACHMENT_DRAG_TYPE, type AttachmentDragPayload } from "../lib/dragTypes";

/// The grip that makes one attachment row draggable into the chat composer.
///
/// A separate handle rather than a draggable row: a recording attachment's row holds a rename input, and a
/// draggable ancestor fights with selecting text inside it. One shared component rather than three copies,
/// so the payload is built in exactly one place - a mismatch between the lists would be invisible until a
/// drop silently did nothing.
export default function AttachmentDragHandle({ scope, ownerId, attachmentId, name }: AttachmentDragPayload) {
  const { t } = useTranslation("workspace");
  return (
    <span
      role="button"
      // Not tabbable: dragging is a mouse gesture, and every attachment already has keyboard-reachable
      // controls. A tab stop per row would be noise for a keyboard user who cannot use it anyway.
      tabIndex={-1}
      draggable
      title={t("dragAttachmentToChat")}
      aria-label={t("dragAttachmentToChat")}
      onDragStart={(e) => {
        // ONLY our own type. Setting text/plain as well would let the recordings panel read this as a
        // recording id being reordered.
        e.dataTransfer.setData(ATTACHMENT_DRAG_TYPE, JSON.stringify({ scope, ownerId, attachmentId, name }));
        e.dataTransfer.effectAllowed = "copy";
      }}
      className="shrink-0 cursor-grab select-none px-1 text-gray-300 hover:text-gray-500 active:cursor-grabbing dark:text-gray-600 dark:hover:text-gray-400"
    >
      ⠿
    </span>
  );
}
