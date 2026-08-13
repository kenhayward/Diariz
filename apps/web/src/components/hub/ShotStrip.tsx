import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { formatDuration } from "../../lib/format";
import type { ShotView } from "../../lib/types";

/**
 * The live thumbnail strip for captures taken during a recording. Shared by the in-app notes popover and
 * the pop-out notes window, which is why it takes `ShotView` (id + stamp + thumbnail) rather than the
 * full `PendingShot`: the pop-out is only ever sent the thumbnail, never the full-resolution image.
 *
 * Deletion is by id. An index would be wrong here - a capture arriving between render and click shifts
 * the list, and in the pop-out that gap is a whole window boundary wide.
 */
export default function ShotStrip({
  shots,
  onDelete,
}: {
  shots: ShotView[];
  onDelete: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");

  // Recomputed whenever the capture set changes, and the previous batch is revoked on cleanup -
  // otherwise a long meeting with many captures leaks one object URL per capture.
  const previews = useMemo(() => shots.map((s) => URL.createObjectURL(s.thumb)), [shots]);
  useEffect(() => () => previews.forEach((url) => URL.revokeObjectURL(url)), [previews]);

  return (
    <ul style={{ display: "flex", flexWrap: "wrap", gap: 4, margin: 0, padding: 0, listStyle: "none" }}>
      {previews.map((url, i) => (
        <li key={shots[i].id} style={{ position: "relative" }}>
          <img
            src={url}
            alt={t("screenshotAlt", { time: formatDuration(shots[i].capturedAtMs) })}
            style={{
              display: "block",
              height: 56,
              width: "auto",
              borderRadius: 6,
              border: "1px solid var(--hub-border)",
            }}
          />
          <button
            type="button"
            aria-label={t("screenshotDelete")}
            onClick={() => onDelete(shots[i].id)}
            style={{
              position: "absolute",
              top: -4,
              right: -4,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              width: 16,
              height: 16,
              borderRadius: "50%",
              border: "none",
              background: "var(--hub-popover-bg)",
              color: "var(--hub-red-text)",
              fontSize: 11,
              lineHeight: 1,
              boxShadow: "0 1px 3px rgba(0, 0, 0, 0.3)",
              cursor: "pointer",
            }}
          >
            ✕
          </button>
        </li>
      ))}
    </ul>
  );
}
