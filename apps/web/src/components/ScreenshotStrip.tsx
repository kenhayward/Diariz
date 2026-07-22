import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Screenshot } from "../lib/types";

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/// A row of capture thumbnails. Used both in the live recorder popover (immediate feedback that the
/// capture area is right) and in the Notes tab's collapsed section - kept free of assumptions about
/// its surroundings so both callers can drop it in as-is. Purely presentational: clicking a thumbnail
/// hands the index to the parent, which owns whether/where a ScreenshotModal opens.
export default function ScreenshotStrip({
  recordingId,
  shots,
  onOpen,
}: {
  recordingId: string;
  shots: Screenshot[];
  onOpen: (index: number) => void;
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
            aria-label={t("screenshotAlt", { time: fmt(shot.capturedAtMs) })}
            className="block overflow-hidden rounded border hover:border-blue-400 dark:border-gray-700 dark:hover:border-blue-500"
          >
            <img
              src={api.screenshotThumbUrl(recordingId, shot.id)}
              alt={t("screenshotAlt", { time: fmt(shot.capturedAtMs) })}
              loading="lazy"
              className="h-20 w-auto"
            />
          </button>
        </li>
      ))}
    </ul>
  );
}
