import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import NotesSection from "../NotesSection";
import type { MeetingNote } from "../../lib/types";
import type { PendingShot } from "../../lib/pendingScreenshots";

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

const IconClose = () => (
  <svg viewBox="0 0 24 24" width="16" height="16" fill="none" aria-hidden="true" focusable="false"
    stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M6 6l12 12M18 6L6 18" />
  </svg>
);

export type NotesPopoverProps = {
  open: boolean;
  onClose: () => void;
  lines: MeetingNote[];
  onAdd: (text: string) => void;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  shots: PendingShot[];
  onDeleteShot: (index: number) => void;
  /// Absent in a plain browser, which is what hides the whole screenshot area.
  onChangeCaptureArea?: () => void;
};

/**
 * The "Notes while recording" popover: a title (with a blinking red recording dot), a subtitle, and the shared
 * NotesSection input + list. It reuses the Recorder's live-notes state/durability/attach-on-stop wholesale -
 * the same `lines` + add/edit/delete callbacks the floating LiveNotesPanel used - so behaviour is unchanged;
 * only the presentation moved into the hub popover. Opened from the pencil icon button while recording.
 */
export default function NotesPopover({
  open,
  onClose,
  lines,
  onAdd,
  onEdit,
  onDelete,
  shots,
  onDeleteShot,
  onChangeCaptureArea,
}: NotesPopoverProps) {
  const { t } = useTranslation("workspace");

  // Local previews for captures that have no server id yet (they're still in the pending stash, not
  // uploaded). Recomputed whenever the capture set changes, and the previous batch is revoked on
  // cleanup - otherwise a long meeting with many captures leaks one object URL per capture.
  const previews = useMemo(() => shots.map((s) => URL.createObjectURL(s.thumb)), [shots]);
  useEffect(() => () => previews.forEach((url) => URL.revokeObjectURL(url)), [previews]);

  return (
    <HubPopover open={open} onClose={onClose} width={400} anchorClassName="right-0" ariaLabel={t("liveNotesTitle")}>
      <div data-testid="notes-popover" style={{ padding: 18, display: "flex", flexDirection: "column", gap: 12 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <span
            aria-hidden
            style={{ width: 9, height: 9, borderRadius: "50%", background: "var(--hub-red)", animation: "blink 1.2s infinite" }}
          />
          <span style={{ fontFamily: "system-ui", fontWeight: 700, fontSize: 17, color: "var(--hub-text)" }}>
            {t("liveNotesTitle")}
          </span>
          <button
            type="button"
            aria-label={t("liveNotesClose")}
            onClick={onClose}
            style={{
              marginLeft: "auto",
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              width: 28,
              height: 28,
              borderRadius: 8,
              border: "none",
              background: "transparent",
              color: "var(--hub-muted)",
              cursor: "pointer",
            }}
            onMouseEnter={(e) => (e.currentTarget.style.background = "var(--hub-surface-hover)")}
            onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
          >
            <IconClose />
          </button>
        </div>
        <p style={{ margin: 0, fontFamily: "system-ui", fontWeight: 400, fontSize: 13, color: "var(--hub-muted)" }}>
          {t("liveNotesHint")}
        </p>
        <div style={{ maxHeight: 320, overflowY: "auto" }}>
          <NotesSection notes={lines} onAdd={onAdd} onEdit={onEdit} onDelete={onDelete} />
        </div>
        {onChangeCaptureArea && (
          <div className="space-y-1 border-t pt-2 dark:border-gray-700">
            <div className="flex items-center justify-between">
              <span className="text-xs font-medium dark:text-gray-300">
                {t("screenshots")} ({shots.length})
              </span>
              <button
                type="button"
                onClick={onChangeCaptureArea}
                className="rounded border px-1.5 py-0.5 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
              >
                {t("screenshotCaptureArea")}
              </button>
            </div>
            <ul className="flex flex-wrap gap-1">
              {previews.map((url, i) => (
                <li key={url} className="relative">
                  <img
                    src={url}
                    alt={t("screenshotAlt", { time: fmt(shots[i].capturedAtMs) })}
                    className="h-14 w-auto rounded border dark:border-gray-700"
                  />
                  <button
                    type="button"
                    aria-label={t("screenshotDelete")}
                    onClick={() => onDeleteShot(i)}
                    className="absolute -right-1 -top-1 rounded-full bg-white px-1 text-xs text-red-600 shadow dark:bg-gray-900 dark:text-red-400"
                  >
                    ✕
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </HubPopover>
  );
}
