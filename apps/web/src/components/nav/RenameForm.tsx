import { useState } from "react";
import { useTranslation } from "react-i18next";

/// The inline "rename this recording" input. Extracted from `RecordingRow` so the Calendar tab's day grid
/// can render the same form over a block - the two surfaces put it in different places (in the row's flow
/// vs. an overlay on the block), but it is the same one-line form either way.
export default function RenameForm({
  initial,
  onSave,
  onCancel,
}: {
  initial: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation("workspace");
  const [value, setValue] = useState(initial);
  return (
    <form
      className="flex min-w-0 flex-1 items-center gap-1"
      onSubmit={(e) => {
        e.preventDefault();
        onSave(value);
      }}
    >
      <input
        autoFocus
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => e.key === "Escape" && onCancel()}
        placeholder={t("recordingNamePlaceholder")}
        aria-label={t("recordingNamePlaceholder")}
        className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-1 text-xs hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800 dark:text-gray-200">
        {t("common:save")}
      </button>
    </form>
  );
}
