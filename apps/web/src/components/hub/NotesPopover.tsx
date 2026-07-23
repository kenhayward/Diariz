import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import NotesSection from "../NotesSection";
import { formatDuration } from "../../lib/format";
import type { MeetingNote } from "../../lib/types";
import type { PendingShot } from "../../lib/pendingScreenshots";

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
  /// Takes a screenshot without closing the popover. Absent in a plain browser, same as onChangeCaptureArea.
  onCapture?: () => void;
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
  onCapture,
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
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              gap: 6,
              borderTop: "1px solid var(--hub-border)",
              paddingTop: 8,
            }}
          >
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
              <span style={{ fontFamily: "system-ui", fontWeight: 600, fontSize: 12, color: "var(--hub-text-2)" }}>
                {t("screenshots")} ({shots.length})
              </span>
              <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                {onCapture && (
                  <button
                    type="button"
                    onClick={onCapture}
                    style={{
                      fontFamily: "system-ui",
                      fontWeight: 500,
                      fontSize: 12,
                      padding: "2px 6px",
                      borderRadius: 6,
                      border: "1px solid var(--hub-border)",
                      background: "transparent",
                      color: "var(--hub-text-2)",
                      cursor: "pointer",
                    }}
                    onMouseEnter={(e) => (e.currentTarget.style.background = "var(--hub-surface-hover)")}
                    onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
                  >
                    {t("screenshotCaptureButton")}
                  </button>
                )}
                <button
                  type="button"
                  onClick={onChangeCaptureArea}
                  style={{
                    fontFamily: "system-ui",
                    fontWeight: 500,
                    fontSize: 12,
                    padding: "2px 6px",
                    borderRadius: 6,
                    border: "1px solid var(--hub-border)",
                    background: "transparent",
                    color: "var(--hub-text-2)",
                    cursor: "pointer",
                  }}
                  onMouseEnter={(e) => (e.currentTarget.style.background = "var(--hub-surface-hover)")}
                  onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
                >
                  {t("screenshotCaptureArea")}
                </button>
              </div>
            </div>
            <ul style={{ display: "flex", flexWrap: "wrap", gap: 4, margin: 0, padding: 0, listStyle: "none" }}>
              {previews.map((url, i) => (
                <li key={url} style={{ position: "relative" }}>
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
                    onClick={() => onDeleteShot(i)}
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
          </div>
        )}
      </div>
    </HubPopover>
  );
}
