import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { renderMarkdown } from "../lib/markdown";

/// Read-only look at what is actually in the chat composer's context pill.
///
/// The pill names an attachment but says nothing about its contents, which matters most for text read off a
/// screen capture: until now the only way to find out what a model had been handed was to send it and infer
/// the answer. Extraction is imperfect by nature - it can misread a digit or drop a region - so being able
/// to read it first is the difference between spotting that and quoting it.
///
/// Deliberately NOT `MarkdownAttachmentEditModal`. That one is a TipTap editor that fetches a saved
/// attachment by id and writes it back; this text is transient client state that has never been saved, and
/// there is nothing here to edit or persist.
export default function ChatAttachmentPreviewModal({
  name,
  text,
  origin,
  onClose,
}: {
  name: string;
  text: string;
  /// Decides how the body is shown, and the distinction is not cosmetic. `ocr` text is Markdown *this app
  /// generated*, so it renders - a table read off a capture should look like a table. `file` text is
  /// whatever was extracted from a user's document, which is not Markdown: rendering it would eat
  /// underscores, asterisks and stray hashes out of ordinary prose.
  origin: "file" | "ocr";
  onClose: () => void;
}) {
  const { t } = useTranslation(["chat", "common"]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    // Backdrop click closes; the panel stops propagation so a click on the text does not.
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4"
      role="dialog"
      aria-modal="true"
      aria-label={name}
      onClick={onClose}
    >
      <div
        className="flex max-h-[80vh] w-full max-w-2xl flex-col rounded-lg bg-white shadow-xl dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center gap-2 border-b px-4 py-3 dark:border-gray-700">
          <h2 className="min-w-0 flex-1 truncate text-base font-semibold dark:text-gray-100">{name}</h2>
          <span className="shrink-0 text-xs text-gray-500 dark:text-gray-400">
            {t("chat:attachmentChars", { count: text.length })}
          </span>
          <button
            type="button"
            aria-label={t("common:close", { defaultValue: "Close" })}
            className="shrink-0 rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            onClick={onClose}
          >
            ✕
          </button>
        </div>

        <div className="overflow-y-auto px-4 py-3">
          {origin === "ocr" ? (
            // Sanitized in renderMarkdown (DOMPurify) before injection. The text is a model's transcription
            // of whatever was on screen, so it is untrusted however ordinary it looks.
            <div
              className="chat-md text-sm dark:text-gray-200"
              dangerouslySetInnerHTML={{ __html: renderMarkdown(text) }}
            />
          ) : (
            <pre className="whitespace-pre-wrap break-words font-mono text-xs dark:text-gray-200">{text}</pre>
          )}
        </div>
      </div>
    </div>
  );
}
