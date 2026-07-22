import { useTranslation } from "react-i18next";
import ScreenshotStrip from "./ScreenshotStrip";
import type { Screenshot } from "../lib/types";

/// The Notes tab's screenshot block. Collapsed by default so the note lines stay the focus, and hidden
/// entirely when the recording has no captures (most recordings won't).
export default function ScreenshotsSection({
  recordingId,
  shots,
  onOpen,
}: {
  recordingId: string;
  shots: Screenshot[];
  onOpen: (index: number) => void;
}) {
  const { t } = useTranslation("workspace");
  if (shots.length === 0) return null;

  return (
    <details className="rounded border p-2 dark:border-gray-700">
      <summary className="cursor-pointer text-sm font-medium dark:text-gray-200">
        {t("screenshots")} ({shots.length})
      </summary>
      <div className="pt-2">
        <ScreenshotStrip recordingId={recordingId} shots={shots} onOpen={onOpen} />
      </div>
    </details>
  );
}
