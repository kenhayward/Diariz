import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { ChatScreenshotRef } from "../lib/types";

/// The screen captures attached to the chat prompt, as thumbnails with a corner remove control.
///
/// Attachments are sticky: they ride every turn until removed, so this row is what tells a user what the
/// model is still being shown. Its own component rather than more markup inside ChatPanel, which is
/// already over a thousand lines.
///
/// The thumbnail URL - not the full capture - is what the browser loads here. The full-resolution image is
/// only ever read server-side, from object storage, when a turn is actually sent.
export default function ChatScreenshotTray({
  shots,
  onRemove,
}: {
  shots: ChatScreenshotRef[];
  onRemove: (shot: ChatScreenshotRef) => void;
}) {
  const { t } = useTranslation("chat");
  if (shots.length === 0) return null;

  return (
    <ul className="mt-2 flex flex-wrap gap-2">
      {shots.map((shot) => (
        // Keyed on the PAIR: a screenshot id is only unique within its recording.
        <li key={`${shot.recordingId}:${shot.screenshotId}`} className="relative">
          <img
            src={api.screenshotThumbUrl(shot.recordingId, shot.screenshotId)}
            alt={t("attachedScreenshot")}
            loading="lazy"
            className="h-14 w-auto rounded border object-cover dark:border-gray-700"
          />
          <button
            type="button"
            onClick={() => onRemove(shot)}
            aria-label={t("removeScreenshot")}
            title={t("removeScreenshot")}
            className="absolute -right-1.5 -top-1.5 flex h-5 w-5 items-center justify-center rounded-full border bg-white text-xs leading-none text-gray-500 shadow hover:text-red-600 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-300"
          >
            ✕
          </button>
        </li>
      ))}
    </ul>
  );
}
