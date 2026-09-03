import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { formatDuration } from "../../lib/format";
import { IconClose, IconPencil, IconPlus } from "./hubGlyphs";
import type { LiveSegment } from "../../lib/liveTranscript";
import type { MeetingNote, ShotView } from "../../lib/types";

/// The three row kinds of the live notes stream. Split out from `LiveNotesStream` so the panel file is
/// about layout and state and these are about one row each.
///
/// All three share a right-aligned monospace stamp column, whose width the panel decides once for the
/// whole list (see `stampColumnPx`) - measuring per row would have the older stamps shift sideways the
/// moment the meeting passed an hour.

const STAMP_FONT = "ui-monospace, Menlo, monospace";

function Stamp({ ms, widthPx, color, weight }: { ms: number; widthPx: number; color: string; weight: number }) {
  return (
    <span
      style={{
        flexShrink: 0,
        width: widthPx,
        textAlign: "right",
        fontFamily: STAMP_FONT,
        fontSize: 11,
        fontWeight: weight,
        color,
        lineHeight: "1.65",
      }}
    >
      {formatDuration(ms)}
    </span>
  );
}

/// A small square icon button for the controls that live inside a row. Deliberately not `HubIconButton`:
/// these are 20px, borderless, and two of them appear only on hover, none of which that component does.
function RowButton({
  label,
  onClick,
  className,
  size = 20,
  color = "var(--hub-muted)",
  children,
}: {
  label: string;
  onClick: () => void;
  /// `hub-row-reveal` hides the button until its row is hovered or focused within. It is a class rather
  /// than an inline `opacity` because an inline value beats the stylesheet's hover rule outright, which
  /// would leave the button permanently invisible while still occupying the tab order.
  className?: string;
  size?: number;
  color?: string;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      className={className}
      style={{
        flexShrink: 0,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        width: size,
        height: size,
        borderRadius: 6,
        border: "none",
        background: "transparent",
        color,
        cursor: "pointer",
        padding: 0,
      }}
    >
      {children}
    </button>
  );
}

export function TranscriptRow({
  segment,
  showSpeaker,
  stampColumnPx,
  onPin,
}: {
  segment: LiveSegment;
  showSpeaker: boolean;
  stampColumnPx: number;
  onPin: (atMs: number) => void;
}) {
  const { t } = useTranslation("recordings");
  const { t: tw } = useTranslation("workspace");

  return (
    <li
      data-testid="stream-transcript"
      className="hub-stream-row"
      style={{ display: "flex", alignItems: "flex-start", gap: 8, padding: "3px 4px", borderRadius: 7 }}
    >
      {/* --hub-muted, not --hub-placeholder: the latter measures 3.37:1 on the dark panel at 11px and
          fails AA, and this is the timestamp the whole redesign exists to put on every line. */}
      <Stamp ms={segment.startMs} widthPx={stampColumnPx} color="var(--hub-muted)" weight={400} />
      <span style={{ minWidth: 0, flex: 1, fontSize: 13, lineHeight: 1.65, color: "var(--hub-text-2)" }}>
        {showSpeaker && (
          <span
            data-testid="stream-speaker"
            // A suggestion is the server asking, not answering. Rendering it identically to a confident
            // match would give a coin flip the authority of a confirmed name.
            data-suggestion={segment.speakerIsSuggestion ? "true" : undefined}
            title={segment.speakerIsSuggestion ? t("liveTranscriptSpeakerGuess") : undefined}
            style={{
              marginRight: 6,
              fontSize: 11,
              fontWeight: 600,
              fontStyle: segment.speakerIsSuggestion ? "italic" : undefined,
              color: "var(--hub-muted)",
            }}
          >
            {segment.speaker}
            {segment.speakerIsSuggestion ? "?" : ""}
          </span>
        )}
        {segment.text}
      </span>
      <RowButton
        label={tw("notesPinToMoment")}
        onClick={() => onPin(segment.startMs)}
        className="hub-row-reveal"
        color="var(--hub-placeholder)"
      >
        <IconPlus size={12} />
      </RowButton>
    </li>
  );
}

export function NoteRow({
  note,
  stampColumnPx,
  onEdit,
  onDelete,
}: {
  note: MeetingNote;
  stampColumnPx: number;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  return (
    <li
      data-testid="stream-note"
      className="hub-note-row hub-stream-row"
      style={{
        display: "flex",
        alignItems: "flex-start",
        gap: 8,
        padding: "5px 4px 5px 0",
        borderRadius: 7,
        // The left rail. An inset shadow rather than a border so the row's height and the stamp column's
        // alignment are identical to the transcript rows around it.
        boxShadow: "inset 2px 0 0 var(--hub-blue)",
      }}
    >
      <Stamp
        ms={note.capturedAtMs ?? 0}
        widthPx={stampColumnPx}
        color="var(--hub-blue-text)"
        weight={600}
      />
      {editing ? (
        <span style={{ display: "flex", minWidth: 0, flex: 1, alignItems: "center", gap: 4 }}>
          <input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            aria-label={t("notesEdit")}
            autoFocus
            style={{
              minWidth: 0,
              flex: 1,
              border: "1px solid var(--hub-field-border)",
              borderRadius: 6,
              background: "var(--hub-surface)",
              color: "var(--hub-text)",
              fontSize: 13,
              padding: "2px 6px",
            }}
          />
          <button
            type="button"
            onClick={() => {
              onEdit(note.id, draft.trim());
              setEditing(false);
            }}
            style={pillButton}
          >
            {t("notesSave")}
          </button>
          <button type="button" onClick={() => setEditing(false)} style={pillButton}>
            {t("notesCancel")}
          </button>
        </span>
      ) : (
        <>
          <span
            style={{ minWidth: 0, flex: 1, fontSize: 13, lineHeight: 1.6, fontWeight: 500, color: "var(--hub-text)", wordBreak: "break-word" }}
          >
            {note.text}
          </span>
          <RowButton
            label={t("notesEdit")}
            onClick={() => {
              setDraft(note.text);
              setEditing(true);
            }}
          >
            <IconPencil size={12} />
          </RowButton>
          <RowButton label={t("notesDelete")} onClick={() => onDelete(note.id)} className="hub-row-delete">
            <IconClose size={12} />
          </RowButton>
        </>
      )}
    </li>
  );
}

const pillButton: React.CSSProperties = {
  flexShrink: 0,
  border: "1px solid var(--hub-field-border)",
  borderRadius: 6,
  background: "transparent",
  color: "var(--hub-text-2)",
  fontSize: 11,
  fontWeight: 600,
  padding: "2px 6px",
  cursor: "pointer",
};

export function CaptureRow({
  shot,
  previewUrl,
  stampColumnPx,
  thumbWidth,
  thumbHeight,
  onDelete,
}: {
  shot: ShotView;
  /// Made by the panel, not here: one object URL per capture for the life of the capture set, so a row
  /// re-rendering (a filter change, a new line arriving) does not mint and leak another.
  previewUrl: string;
  stampColumnPx: number;
  thumbWidth: number;
  thumbHeight: number;
  onDelete: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");

  return (
    <li
      data-testid="stream-capture"
      style={{ display: "flex", alignItems: "flex-start", gap: 8, padding: "3px 4px" }}
    >
      <Stamp ms={shot.capturedAtMs} widthPx={stampColumnPx} color="var(--hub-muted)" weight={400} />
      <div style={{ position: "relative", width: thumbWidth, flexShrink: 0 }}>
        <img
          src={previewUrl}
          alt={t("screenshotAlt", { time: formatDuration(shot.capturedAtMs) })}
          style={{
            display: "block",
            width: thumbWidth,
            height: thumbHeight,
            objectFit: "cover",
            borderRadius: 8,
            border: "1px solid var(--hub-border)",
            background: "var(--hub-surface)",
          }}
        />
        <div
          style={{
            position: "absolute",
            insetInline: 0,
            bottom: 0,
            display: "flex",
            justifyContent: "flex-end",
            gap: 4,
            padding: 5,
            borderRadius: "0 0 8px 8px",
            background: "linear-gradient(transparent, rgba(6, 11, 22, 0.85))",
          }}
        >
          <button
            type="button"
            aria-label={t("screenshotDelete")}
            title={t("screenshotDelete")}
            onClick={() => onDelete(shot.id)}
            style={{
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              width: 22,
              height: 22,
              borderRadius: 6,
              border: "none",
              background: "rgba(6, 11, 22, 0.8)",
              color: "var(--hub-red-text)",
              cursor: "pointer",
              padding: 0,
            }}
          >
            <IconClose size={12} />
          </button>
        </div>
      </div>
    </li>
  );
}

/// One object URL per capture, revoked when the set changes and on unmount.
///
/// Without the revoke a long meeting leaks one blob URL per capture; without the memo every unrelated
/// re-render (a transcript line arriving, which is every few seconds) mints a fresh set and orphans the
/// previous one. Lifted out of the old `ShotStrip` unchanged, tests and all.
///
/// Keyed by capture id rather than returned as a parallel array, because in the stream the captures are
/// interleaved with notes and transcript lines - there is no shared index to line the two up by.
export function useShotPreviews(shots: ShotView[]): Map<string, string> {
  const previews = useMemo(
    () => new Map(shots.map((s) => [s.id, URL.createObjectURL(s.thumb)])),
    [shots],
  );
  useEffect(() => () => previews.forEach((url) => URL.revokeObjectURL(url)), [previews]);
  return previews;
}
