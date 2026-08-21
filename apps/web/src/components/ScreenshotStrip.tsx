import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { SCREENSHOT_DRAG_TYPE } from "../lib/dragTypes";
import { formatDuration } from "../lib/format";
import type { Screenshot } from "../lib/types";

/// A row of capture thumbnails, used by the Notes tab's collapsed Screenshots section
/// (`ScreenshotsSection`). The live recorder popover and the transcript's screenshot rows each hand-roll
/// their own thumbnail markup rather than reusing this component (a three-way extraction is a tracked
/// follow-up). Purely presentational: clicking a thumbnail hands the index to the parent, which owns
/// whether/where a ScreenshotModal opens.
///
/// With `draggable`, a thumbnail can be dragged into the chat composer to attach it to a prompt for a
/// vision model. The payload goes under its own MIME type rather than `text/plain` so the composer cannot
/// mistake an arbitrary dragged word or link for a capture, and so dragging a thumbnail anywhere else in
/// the page does nothing surprising. The type itself lives in lib/dragTypes, beside the app's other drag
/// payloads - the upload drop zones have to know about it too, so they do not mistake an image drag for a
/// file drag.
export default function ScreenshotStrip({
  recordingId,
  shots,
  onOpen,
  draggable = false,
}: {
  recordingId: string;
  shots: Screenshot[];
  onOpen: (index: number) => void;
  /// Opt-in: only the Notes tab wires the chat gesture, so nothing else grows a drag behaviour it never
  /// asked for.
  draggable?: boolean;
}) {
  const { t } = useTranslation("workspace");

  if (shots.length === 0)
    return <p className="text-xs text-gray-400 dark:text-gray-500">{t("screenshotsEmpty")}</p>;

  return (
    <ul className="flex flex-wrap gap-2">
      {shots.map((shot, i) => (
        <li key={shot.id}>
          <button
            type="button"
            onClick={() => onOpen(i)}
            draggable={draggable}
            onDragStart={(e) => {
              if (!draggable) return;
              e.dataTransfer.setData(
                SCREENSHOT_DRAG_TYPE,
                JSON.stringify({ recordingId, screenshotId: shot.id, capturedAtMs: shot.capturedAtMs }),
              );
              e.dataTransfer.effectAllowed = "copy";
            }}
            aria-label={t("screenshotAlt", { time: formatDuration(shot.capturedAtMs) })}
            className="block overflow-hidden rounded border hover:border-blue-400 dark:border-gray-700 dark:hover:border-blue-500"
          >
            <img
              src={api.screenshotThumbUrl(recordingId, shot.id)}
              alt={t("screenshotAlt", { time: formatDuration(shot.capturedAtMs) })}
              loading="lazy"
              className="h-20 w-auto"
            />
          </button>
        </li>
      ))}
    </ul>
  );
}
