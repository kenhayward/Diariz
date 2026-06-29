import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { formatBytes } from "../lib/format";
import type { Attachment } from "../lib/types";
import { openAttachment } from "./AttachmentsSplitButton";

/// Manage a recording's attachments: add files or a URL, rename, open, and remove. The parent owns the
/// list (react-query); every change calls `onChange` to refetch.
export default function AttachmentsModal({
  recordingId,
  attachments,
  onClose,
  onChange,
}: {
  recordingId: string;
  attachments: Attachment[];
  onClose: () => void;
  onChange: () => void;
}) {
  const { t } = useTranslation("workspace");
  const fileInput = useRef<HTMLInputElement>(null);
  const [url, setUrl] = useState("");
  const [urlName, setUrlName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run(fn: () => Promise<unknown>, fallback: string) {
    setBusy(true);
    setError(null);
    try {
      await fn();
      onChange();
    } catch (e) {
      setError(apiErrorMessage(e, t(fallback)));
    } finally {
      setBusy(false);
    }
  }

  async function onFiles(files: FileList | null) {
    if (!files || files.length === 0) return;
    await run(async () => {
      for (const f of Array.from(files)) await api.addFileAttachment(recordingId, f);
    }, "errAddAttachment");
    if (fileInput.current) fileInput.current.value = "";
  }

  function addUrl() {
    const trimmed = url.trim();
    if (!trimmed) return;
    void run(async () => {
      await api.addUrlAttachment(recordingId, trimmed, urlName.trim() || undefined);
      setUrl("");
      setUrlName("");
    }, "errAddAttachment");
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
      <div className="flex max-h-[80vh] w-full max-w-2xl flex-col rounded-lg bg-white p-5 shadow-xl dark:bg-gray-900">
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("attachmentsTitle")}</h2>

        {error && (
          <p className="mb-2 rounded bg-red-50 p-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{error}</p>
        )}

        <div className="min-h-0 flex-1 overflow-auto">
          {attachments.length === 0 ? (
            <p className="py-4 text-sm text-gray-500 dark:text-gray-400">{t("noAttachments")}</p>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-xs text-gray-400 dark:text-gray-500">
                  <th className="py-1 font-medium">{t("attachmentName")}</th>
                  <th className="py-1 font-medium">{t("attachmentSize")}</th>
                  <th className="py-1" />
                </tr>
              </thead>
              <tbody>
                {attachments.map((a) => (
                  <tr key={a.id} className="border-t dark:border-gray-700">
                    <td className="py-1 pr-2">
                      <input
                        defaultValue={a.name}
                        aria-label={t("attachmentName")}
                        onBlur={(e) => {
                          const v = e.target.value.trim();
                          if (v && v !== a.name) void run(() => api.renameAttachment(recordingId, a.id, v), "errRenameAttachment");
                        }}
                        className="w-full rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                      />
                    </td>
                    <td className="py-1 pr-2 whitespace-nowrap text-gray-500 dark:text-gray-400">
                      {a.kind === "Url" ? t("attachmentUrlType") : formatBytes(a.sizeBytes)}
                    </td>
                    <td className="py-1 text-right whitespace-nowrap">
                      <button
                        type="button"
                        onClick={() => openAttachment(recordingId, a)}
                        className="mr-2 text-blue-600 hover:underline dark:text-blue-400"
                      >
                        {t("openAttachment")}
                      </button>
                      <button
                        type="button"
                        disabled={busy}
                        onClick={() => {
                          if (window.confirm(t("confirmDeleteAttachment")))
                            void run(() => api.deleteAttachment(recordingId, a.id), "errDeleteAttachment");
                        }}
                        className="text-red-600 hover:underline dark:text-red-400"
                      >
                        {t("removeAttachment")}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        {/* Add controls */}
        <div className="mt-4 space-y-2 border-t pt-3 dark:border-gray-700">
          <div className="flex items-center gap-2">
            <input ref={fileInput} type="file" multiple hidden onChange={(e) => void onFiles(e.target.files)} />
            <button
              type="button"
              disabled={busy}
              onClick={() => fileInput.current?.click()}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("addFile")}
            </button>
          </div>
          <div className="flex items-center gap-2">
            <input
              value={url}
              onChange={(e) => setUrl(e.target.value)}
              placeholder={t("urlAddressPlaceholder")}
              aria-label={t("urlAddressPlaceholder")}
              className="flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
            <input
              value={urlName}
              onChange={(e) => setUrlName(e.target.value)}
              placeholder={t("urlNamePlaceholder")}
              aria-label={t("urlNamePlaceholder")}
              className="w-40 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
            <button
              type="button"
              disabled={busy || !url.trim()}
              onClick={addUrl}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("addUrl")}
            </button>
          </div>
        </div>

        <div className="mt-4 flex justify-end">
          <button
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </div>
  );
}
