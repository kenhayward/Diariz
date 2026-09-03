import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import LiveNotesStream from "./LiveNotesStream";
import { IconClose, IconPopOut } from "./hubGlyphs";
import { formatDuration } from "../../lib/format";
import type { LiveTranscript } from "../../lib/liveTranscript";
import type { MeetingNote } from "../../lib/types";
import type { PendingShot } from "../../lib/pendingScreenshots";

export type NotesPopoverProps = {
  open: boolean;
  onClose: () => void;
  lines: MeetingNote[];
  /// `atMs` present means the note was pinned to a moment earlier in the meeting; absent means it
  /// follows the recorded clock, which only the host can read.
  onAdd: (text: string, atMs?: number) => void;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  shots: PendingShot[];
  /// Delete one capture, addressed by id rather than position - captures can arrive at any moment, so
  /// an index read at render time may not be the one the user clicked by the time the click lands.
  onDeleteShot: (id: string) => void;
  /// The recorded clock, in ms. Drives the header's elapsed time and the composer's stamp badge.
  elapsedMs: number;
  /// The transcript of the meeting in progress, or absent when live transcription is not running -
  /// an older server, a deployment without the hardware for it, or a capture that began before the
  /// server could be reached. Absent hides the status line; the stream itself still runs.
  liveTranscript?: LiveTranscript;
  /// How far behind the meeting the transcript is, in whole seconds.
  liveLagSeconds?: number;
  /// The server has stopped transcribing live. Capture is unaffected.
  liveDegraded?: boolean;
  /// Absent in a plain browser, which is what hides the whole capture area.
  onChangeCaptureArea?: () => void;
  /// Takes a screenshot without closing the popover. Absent in a plain browser, same as
  /// onChangeCaptureArea: the two arrive together (both gated on the shell bridge), so either one
  /// missing hides the capture controls and the Captures chip.
  onCapture?: () => void;
  /// Whether this recording has a capture area yet. Capturing without one opens the area picker, and BOTH
  /// buttons then no-op until that picker is dismissed - which reads as the popover having frozen. So capture
  /// stays disabled until the area is set, making "set the area" the visible first step. Defaults to true:
  /// callers that know nothing about the shell's area state (a plain browser, older tests) must not be gated.
  captureAreaSet?: boolean;
  /// Whether auto-capture is running, and the toggle for it. Absent in a plain browser and on a desktop
  /// shell predating the feature, which is what hides the control.
  autoCapture?: boolean;
  onToggleAutoCapture?: () => void;
  /// Detach the notes into their own always-on-top window. Absent in a plain browser, which is what
  /// hides the control - only the desktop shell can pin a window above a full-screen call.
  onPopOut?: () => void;
};

const headerButton: React.CSSProperties = {
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
};

/**
 * The "Notes while recording" popover: a header carrying the recording dot and the elapsed clock, and
 * under it the shared `LiveNotesStream` - notes, captures and the live transcript on one stamped
 * timeline, with the composer docked at the bottom.
 *
 * There used to be a Notes tab and a Transcript tab here. The tabs are gone: what someone wants while a
 * meeting runs is to see what was just said and write about it in the same movement, and a tab put a
 * click between those two things at exactly the moment they had least attention to spare.
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
  elapsedMs,
  liveTranscript,
  liveLagSeconds,
  liveDegraded,
  onChangeCaptureArea,
  onCapture,
  captureAreaSet = true,
  autoCapture,
  onToggleAutoCapture,
  onPopOut,
}: NotesPopoverProps) {
  const { t } = useTranslation("workspace");

  return (
    <HubPopover open={open} onClose={onClose} width={400} anchorClassName="right-0" ariaLabel={t("liveNotesTitle")}>
      <div data-testid="notes-popover" style={{ display: "flex", flexDirection: "column" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 10, padding: "14px 14px 0" }}>
          <span
            aria-hidden
            style={{ width: 9, height: 9, borderRadius: "50%", background: "var(--hub-red)", animation: "blink 1.2s infinite" }}
          />
          <span style={{ fontFamily: "system-ui", fontWeight: 700, fontSize: 15, color: "var(--hub-text)" }}>
            {t("liveNotesRecording")}
          </span>
          <span
            data-testid="notes-elapsed"
            style={{
              fontFamily: "ui-monospace, Menlo, monospace",
              fontWeight: 500,
              fontSize: 13,
              color: "var(--hub-muted)",
            }}
          >
            {formatDuration(elapsedMs)}
          </span>
          {onPopOut && (
            <button
              type="button"
              aria-label={t("notesPopOut")}
              title={t("notesPopOut")}
              onClick={onPopOut}
              // Whichever control comes first carries the auto margin, so the pair stays right-aligned.
              style={{ ...headerButton, marginLeft: "auto" }}
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
            style={{ ...headerButton, marginLeft: onPopOut ? 0 : "auto" }}
            onMouseEnter={(e) => (e.currentTarget.style.background = "var(--hub-surface-hover)")}
            onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
          >
            <IconClose />
          </button>
        </div>

        <LiveNotesStream
          variant="popover"
          lines={lines}
          shots={shots}
          elapsedMs={elapsedMs}
          onAdd={onAdd}
          onEdit={onEdit}
          onDelete={onDelete}
          onDeleteShot={onDeleteShot}
          liveTranscript={liveTranscript}
          liveLagSeconds={liveLagSeconds}
          liveDegraded={liveDegraded}
          // The two capture handlers arrive together or not at all (the recorder gates both on one
          // `canCaptureScreenshots()` check), so a half-supplied pair hides the capture controls rather
          // than rendering a row with a gap in it.
          capture={
            onChangeCaptureArea && onCapture
              ? {
                  captureAreaSet,
                  autoCapture,
                  onToggleAutoCapture,
                  onCapture,
                  onChangeArea: onChangeCaptureArea,
                }
              : undefined
          }
        />
      </div>
    </HubPopover>
  );
}
