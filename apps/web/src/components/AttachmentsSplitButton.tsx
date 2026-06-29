import { useState, useRef, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Attachment } from "../lib/types";

/// Open an attachment using the browser's default behaviour: a file streams from a self-authenticating
/// URL (PDFs/images render inline, others download); a URL attachment opens its address.
export function openAttachment(recordingId: string, a: Attachment) {
  const href = a.kind === "Url" ? a.url ?? "" : api.attachmentContentUrl(recordingId, a.id);
  if (href) window.open(href, "_blank", "noopener");
}

/// "Attachments (N)" split button: the main part opens the manage modal; the caret opens a dropdown
/// listing each attachment to open it directly.
export default function AttachmentsSplitButton({
  recordingId,
  attachments,
  onManage,
}: {
  recordingId: string;
  attachments: Attachment[];
  onManage: () => void;
}) {
  const { t } = useTranslation("workspace");
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [open]);

  return (
    <div ref={ref} className="relative inline-flex">
      <button
        type="button"
        onClick={onManage}
        className="rounded-l border border-r-0 px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
      >
        {t("attachmentsCount", { count: attachments.length })}
      </button>
      <button
        type="button"
        aria-label={t("openAttachmentMenu")}
        disabled={attachments.length === 0}
        onClick={() => setOpen((v) => !v)}
        className="rounded-r border px-1.5 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-40 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
      >
        ▾
      </button>
      {open && attachments.length > 0 && (
        <div className="absolute right-0 top-full z-20 mt-1 max-h-72 w-64 overflow-auto rounded-lg border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900">
          {attachments.map((a) => (
            <button
              key={a.id}
              type="button"
              onClick={() => {
                openAttachment(recordingId, a);
                setOpen(false);
              }}
              className="block w-full truncate px-3 py-1.5 text-left text-sm hover:bg-gray-100 dark:text-gray-200 dark:hover:bg-gray-800"
              title={a.name}
            >
              {a.kind === "Url" ? "🔗 " : "📄 "}
              {a.name}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
