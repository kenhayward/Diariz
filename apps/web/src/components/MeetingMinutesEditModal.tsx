import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useEditor, EditorContent, type Editor } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { Table } from "@tiptap/extension-table";
import { TableRow } from "@tiptap/extension-table-row";
import { TableHeader } from "@tiptap/extension-table-header";
import { TableCell } from "@tiptap/extension-table-cell";
import { Markdown } from "tiptap-markdown";

/// A WYSIWYG editor for meeting minutes. The canonical stored format is Markdown: the editor is seeded
/// from Markdown and serialises back to Markdown on save (via tiptap-markdown), so headings, lists, tables,
/// and bold round-trip. The editor content area auto-grows and scrolls within a bounded height.
export default function MeetingMinutesEditModal({
  initial,
  onClose,
  onSave,
}: {
  initial: string;
  onClose: () => void;
  onSave: (markdown: string) => Promise<void> | void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const [saving, setSaving] = useState(false);

  const editor = useEditor({
    extensions: [
      StarterKit,
      Table.configure({ resizable: false }),
      TableRow,
      TableHeader,
      TableCell,
      Markdown.configure({ html: false, tightLists: true, transformPastedText: true }),
    ],
    content: initial,
  });

  async function save() {
    if (!editor) return;
    setSaving(true);
    try {
      // tiptap-markdown augments editor.storage with a `markdown` helper (untyped here).
      const markdown = (editor.storage as unknown as { markdown: { getMarkdown(): string } }).markdown.getMarkdown();
      await onSave(markdown);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
      <div className="flex max-h-[85vh] w-full max-w-3xl flex-col rounded-lg bg-white p-5 shadow-xl dark:bg-gray-900">
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("workspace:minutesEditTitle")}</h2>

        <MinutesToolbar editor={editor} t={t} />

        <div className="min-h-0 flex-1 overflow-auto rounded border dark:border-gray-700">
          <EditorContent
            editor={editor}
            className="minutes-editor px-3 py-2 text-sm text-gray-800 focus:outline-none dark:text-gray-100
              [&_.ProseMirror]:min-h-[16rem] [&_.ProseMirror]:outline-none
              [&_h1]:mb-2 [&_h1]:mt-1 [&_h1]:text-lg [&_h1]:font-bold
              [&_h2]:mb-2 [&_h2]:mt-3 [&_h2]:text-base [&_h2]:font-semibold
              [&_ul]:my-1 [&_ul]:list-disc [&_ul]:pl-6 [&_ol]:my-1 [&_ol]:list-decimal [&_ol]:pl-6
              [&_table]:my-2 [&_table]:border-collapse [&_th]:border [&_th]:px-2 [&_th]:py-1 [&_th]:font-semibold
              [&_td]:border [&_td]:px-2 [&_td]:py-1 [&_p]:my-1"
          />
        </div>

        <div className="mt-4 flex justify-end gap-2">
          <button
            onClick={onClose}
            disabled={saving}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            onClick={save}
            disabled={saving || !editor}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {t("common:save")}
          </button>
        </div>
      </div>
    </div>
  );
}

/// A minimal formatting toolbar for the WYSIWYG editor.
function MinutesToolbar({ editor, t }: { editor: Editor | null; t: (k: string) => string }) {
  if (!editor) return null;
  const btn = "rounded px-2 py-1 text-xs font-medium hover:bg-gray-100 dark:hover:bg-gray-800";
  return (
    <div className="mb-2 flex flex-wrap gap-1 border-b pb-2 dark:border-gray-700">
      <button type="button" className={btn} title={t("workspace:mmBold")}
        onClick={() => editor.chain().focus().toggleBold().run()}><strong>B</strong></button>
      <button type="button" className={btn} title={t("workspace:mmHeading")}
        onClick={() => editor.chain().focus().toggleHeading({ level: 2 }).run()}>H2</button>
      <button type="button" className={btn} title={t("workspace:mmBulletList")}
        onClick={() => editor.chain().focus().toggleBulletList().run()}>• List</button>
      <button type="button" className={btn} title={t("workspace:mmOrderedList")}
        onClick={() => editor.chain().focus().toggleOrderedList().run()}>1. List</button>
      <button type="button" className={btn} title={t("workspace:mmTable")}
        onClick={() => editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run()}>
        {t("workspace:mmTable")}
      </button>
    </div>
  );
}
