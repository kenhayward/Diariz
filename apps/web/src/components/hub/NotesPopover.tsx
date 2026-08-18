import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import NotesSection from "../NotesSection";
import ShotStrip from "./ShotStrip";
import CaptureControls from "./CaptureControls";
import type { MeetingNote } from "../../lib/types";
import type { PendingShot } from "../../lib/pendingScreenshots";

const IconPopOut = () => (
  <svg viewBox="0 0 24 24" width="16" height="16" fill="none" aria-hidden="true" focusable="false"
    stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M14 4h6v6M20 4l-8 8M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" />
  </svg>
);

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
  /// Delete one capture, addressed by id rather than position - captures can arrive at any moment, so
  /// an index read at render time may not be the one the user clicked by the time the click lands.
  onDeleteShot: (id: string) => void;
  /// Absent in a plain browser, which is what hides the whole screenshot area.
  onChangeCaptureArea?: () => void;
  /// Takes a screenshot without closing the popover. Absent in a plain browser, same as onChangeCaptureArea:
  /// the two arrive together (both gated on the shell bridge), so either one missing hides the section.
  onCapture?: () => void;
  /// Whether this recording has a capture area yet. Capturing without one opens the area picker, and BOTH
  /// buttons then no-op until that picker is dismissed - which reads as the popover having frozen. So capture
  /// stays disabled until the area is set, making "set the area" the visible first step. Defaults to true:
  /// callers that know nothing about the shell's area state (a plain browser, older tests) must not be gated.
  captureAreaSet?: boolean;
  /// Detach the notes into their own always-on-top window. Absent in a plain browser, which is what
  /// hides the control - only the desktop shell can pin a window above a full-screen call.
  onPopOut?: () => void;
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
  captureAreaSet = true,
  onPopOut,
}: NotesPopoverProps) {
  const { t } = useTranslation("workspace");

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
          {onPopOut && (
            <button
              type="button"
              aria-label={t("notesPopOut")}
              title={t("notesPopOut")}
              onClick={onPopOut}
              style={{
                // Whichever control comes first carries the auto margin, so the pair stays right-aligned.
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
              <IconPopOut />
            </button>
          )}
          <button
            type="button"
            aria-label={t("liveNotesClose")}
            onClick={onClose}
            style={{
              marginLeft: onPopOut ? 0 : "auto",
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
        {onChangeCaptureArea && onCapture && (
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
              <CaptureControls
                captureAreaSet={captureAreaSet}
                onCapture={onCapture}
                onChangeArea={onChangeCaptureArea}
              />
            </div>
            <ShotStrip shots={shots} onDelete={onDeleteShot} />
          </div>
        )}
      </div>
    </HubPopover>
  );
}
