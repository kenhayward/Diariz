import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import NotesSection from "../NotesSection";
import type { MeetingNote } from "../../lib/types";

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
};

/**
 * The "Notes while recording" popover: a title (with a blinking red recording dot), a subtitle, and the shared
 * NotesSection input + list. It reuses the Recorder's live-notes state/durability/attach-on-stop wholesale -
 * the same `lines` + add/edit/delete callbacks the floating LiveNotesPanel used - so behaviour is unchanged;
 * only the presentation moved into the hub popover. Opened from the pencil icon button while recording.
 */
export default function NotesPopover({ open, onClose, lines, onAdd, onEdit, onDelete }: NotesPopoverProps) {
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
      </div>
    </HubPopover>
  );
}
