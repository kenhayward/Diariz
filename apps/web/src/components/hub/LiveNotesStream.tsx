import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import CaptureControls from "./CaptureControls";
import { CaptureRow, NoteRow, TranscriptRow, useShotPreviews } from "./notesStreamRows";
import { buildStream, stampColumnPx, streamCounts, type StreamFilter } from "../../lib/notesStream";
import { formatDuration } from "../../lib/format";
import { IconChatArrow, IconCheck } from "./hubGlyphs";
import type { LiveSegment, LiveTranscript } from "../../lib/liveTranscript";
import type { MeetingNote, ShotView } from "../../lib/types";

export type LiveNotesStreamCapture = {
  captureAreaSet: boolean;
  autoCapture?: boolean;
  /// Absent on a shell predating auto-capture, which hides the toggle rather than showing a dead one.
  onToggleAutoCapture?: () => void;
  onCapture: () => void;
  onChangeArea: () => void;
  /// Why the row is unavailable, or undefined when it is usable. The pop-out passes its offline copy.
  unavailableReason?: string;
};

export type LiveNotesStreamProps = {
  lines: MeetingNote[];
  shots: ShotView[];
  /// The meeting in progress, or absent when live transcription is not running - an older server, a
  /// deployment without the hardware, or a capture that began before the server could be reached.
  /// Absent hides the status line entirely; the stream still carries notes and captures.
  liveTranscript?: LiveTranscript;
  liveLagSeconds?: number;
  liveDegraded?: boolean;
  /// The recorded clock. Drives the composer's badge and, once past an hour, the stamp column's width.
  elapsedMs: number;
  onAdd: (text: string, atMs?: number) => void;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  onDeleteShot: (id: string) => void;
  /// Absent where the host cannot capture at all, which hides the capture controls AND the Captures
  /// chip - a plain browser has no captures to filter for. Both handlers are required rather than
  /// optional so the pairing rule the popover has always enforced is a type error to break.
  capture?: LiveNotesStreamCapture;
  /// Temporarily inert: the composer stays visible but refuses input. Distinct from having no `onAdd` -
  /// the pop-out uses this when it has lost its host, where a vanished box would read as lost notes.
  disabled?: boolean;
  /// `popover` is the 400px inline panel (fixed 300px stream, 13px input, smaller thumbnails); `window`
  /// is the detached one, where the stream takes the height that is going spare.
  variant: "popover" | "window";
  /// An extra control at the left end of the action row, beside "Use in chat".
  actionSlot?: ReactNode;
  /// An extra element at the right end of the status line, beside the capture confirmation.
  statusSlot?: ReactNode;
  /// Put the running meeting into the chat prompt. Absent hides the button entirely - without a live
  /// session there is no server-side transcript to reference, exactly as the Transcript tab used to be
  /// hidden when nothing was being transcribed.
  onTranscriptToChat?: () => void;
  /// Put one capture into the chat prompt, by the id it carries in this panel.
  onShotToChat?: (id: string) => void;
  /// The recording currently streaming, when there is one. Gates the drag payload and decides which of
  /// the two reasons a capture's Chat button gives when it cannot act.
  liveRecordingId?: string;
  /// A counter the host bumps to ask for the composer. Deliberately a number, not a boolean: two
  /// consecutive presses of the note hotkey must both land, and `true -> true` is not a change React
  /// would run an effect for.
  focusRequest?: number;
  /// The global accelerators as the shell actually registered them, already formatted for the platform.
  /// Absent in a plain browser - and on a shell predating the notes hotkeys - which hides the hint line
  /// rather than promising keys that do nothing.
  hotkeys?: { capture: string; note: string; transcriptChat: string };
  /// Draw only the status line and the composer. Compact mode in the detached window, for a call that
  /// has taken the screen.
  compact?: boolean;
};

/// How long a confirmation stays before the control comes back. Long enough to be read without looking
/// for it, short enough that the button is there again before the next thought.
const TRANSCRIPT_SENT_MS = 2_600;
const CAPTURE_SENT_MS = 2_400;

const VARIANTS = {
  popover: { streamHeight: 300, inputFontSize: 13, thumbWidth: 150, thumbHeight: 84, autoFocus: true },
  window: { streamHeight: null, inputFontSize: 14, thumbWidth: 170, thumbHeight: 96, autoFocus: false },
} as const;

/// Once the list is within this many pixels of the bottom it counts as "at the tail", and new rows
/// scroll it. Larger than zero because a scroll position lands on a fraction of a pixel often enough
/// that an exact comparison would silently stop following the meeting.
const TAIL_SLACK_PX = 24;

/// One shared empty array for "no live transcript". A fresh `[]` per render would change the identity
/// the stream memo is keyed on, so it would rebuild the whole list on every tick of the clock.
const NO_SEGMENTS: LiveSegment[] = [];

/**
 * The live notes panel's body: the action row, the filter chips, the one stamped stream of notes,
 * captures and transcript lines, and the composer docked under it.
 *
 * Rendered by both hosts - the inline popover in the recording bar and the detached `/notes-popout`
 * window - which is the point. The two carried near-identical copies of a notes list and a transcript
 * tab before this, and had already drifted on when capture is disabled.
 *
 * What it deliberately does not own: the header (each host has its own - a popover title bar versus a
 * window one), the clock (handed in as `elapsedMs`, because only the host knows the pause-aware
 * recorded time), and the notes themselves. Every edit goes back out through a callback.
 */
export default function LiveNotesStream({
  lines,
  shots,
  liveTranscript,
  liveLagSeconds,
  liveDegraded,
  elapsedMs,
  onAdd,
  onEdit,
  onDelete,
  onDeleteShot,
  capture,
  disabled = false,
  variant,
  actionSlot,
  statusSlot,
  onTranscriptToChat,
  onShotToChat,
  liveRecordingId,
  focusRequest,
  hotkeys,
  compact = false,
}: LiveNotesStreamProps) {
  const { t } = useTranslation("workspace");
  const { t: tr } = useTranslation("recordings");
  const v = VARIANTS[variant];

  const [filter, setFilter] = useState<StreamFilter>("all");
  const [draft, setDraft] = useState("");
  /// The moment the composer files at, taken over from a transcript line; null means follow the clock.
  const [pinnedAtMs, setPinnedAtMs] = useState<number | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const segments = liveTranscript?.segments ?? NO_SEGMENTS;
  // Recomputed, never appended to: `useLiveTranscript` replaces its segments wholesale on every append,
  // so anything accumulated across renders would duplicate corrected lines.
  const items = useMemo(
    () => buildStream({ lines, shots, segments, filter }),
    [lines, shots, segments, filter],
  );
  const counts = useMemo(() => streamCounts({ lines, shots }), [lines, shots]);
  const stampPx = stampColumnPx(elapsedMs);
  const previews = useShotPreviews(shots);

  // ---- Auto-scroll ----

  const streamRef = useRef<HTMLUListElement>(null);
  // Read BEFORE the browser paints this render, so it describes where the list was when the user last
  // looked at it rather than where the new content has just put it.
  const wasAtTail = useRef(true);
  useLayoutEffect(() => {
    const el = streamRef.current;
    if (!el) return;
    if (wasAtTail.current) el.scrollTop = el.scrollHeight;
    // Recorded for the NEXT change. Someone who has scrolled up to re-read something said five minutes
    // ago must not be yanked back to the bottom every time a new line lands.
    wasAtTail.current = el.scrollTop + el.clientHeight >= el.scrollHeight - TAIL_SLACK_PX;
  }, [items]);

  function onStreamScroll() {
    const el = streamRef.current;
    if (el) wasAtTail.current = el.scrollTop + el.clientHeight >= el.scrollHeight - TAIL_SLACK_PX;
  }

  // The note hotkey, arriving from the shell by way of the host. Skipped on the first render, so merely
  // opening the panel is not a request - `autoFocus` covers that - and so the detached window does not
  // grab focus out of whatever the user was typing the moment it appears.
  const seenFocusRequest = useRef(focusRequest);
  useEffect(() => {
    if (focusRequest === undefined || focusRequest === seenFocusRequest.current) return;
    seenFocusRequest.current = focusRequest;
    inputRef.current?.focus();
  }, [focusRequest]);

  // ---- Confirmations ----

  // Transient, and cleared on unmount: the panel is a popover the user can close mid-confirmation, and a
  // timer writing to a gone component is exactly the update React complains about.
  const [transcriptSent, setTranscriptSent] = useState(false);
  const [captureSent, setCaptureSent] = useState(false);
  useEffect(() => {
    if (!transcriptSent) return;
    const id = setTimeout(() => setTranscriptSent(false), TRANSCRIPT_SENT_MS);
    return () => clearTimeout(id);
  }, [transcriptSent]);
  useEffect(() => {
    if (!captureSent) return;
    const id = setTimeout(() => setCaptureSent(false), CAPTURE_SENT_MS);
    return () => clearTimeout(id);
  }, [captureSent]);

  // ---- Composer ----

  function pin(atMs: number) {
    setPinnedAtMs(atMs);
    inputRef.current?.focus();
  }

  function file() {
    const text = draft.trim();
    if (!text || disabled) return;
    onAdd(text, pinnedAtMs ?? undefined);
    setDraft("");
    // The pin is spent. Leaving it set would file the next note - about whatever is being said now - at
    // the same old moment, which is the one mistake this control can make invisibly.
    setPinnedAtMs(null);
  }

  // Hidden without a live transcript, exactly as the Transcript tab used to be: with no live session
  // there is no server-side transcript for the chat to reference, so the button would attach a meeting
  // the model can read nothing of.
  const canSendTranscript = Boolean(onTranscriptToChat && liveTranscript);

  const status = liveDegraded
    ? { short: tr("liveStatusPaused"), long: tr("liveTranscriptDegraded") }
    : (liveLagSeconds ?? 0) > 0
      ? {
          short: tr("liveStatusBehind", { seconds: liveLagSeconds }),
          long: tr("liveTranscriptBehind", { seconds: liveLagSeconds }),
        }
      : { short: tr("liveStatusLive"), long: tr("liveTranscriptLive") };

  // Omits an accelerator the shell could not register (an empty string), rather than printing a gap
  // where a key should be.
  const hotkeyHint =
    hotkeys && (hotkeys.note || hotkeys.capture || hotkeys.transcriptChat)
      ? t("notesHotkeyHint", {
          note: hotkeys.note,
          capture: hotkeys.capture,
          chat: hotkeys.transcriptChat,
        })
      : null;

  const chips: { id: StreamFilter; label: string }[] = [
    { id: "all", label: t("notesFilterAll") },
    { id: "notes", label: t("notesFilterNotes", { n: counts.notes }) },
    // No capture bridge means no captures will ever exist, so the chip would filter to a permanent
    // empty state.
    ...(capture ? [{ id: "captures" as const, label: t("notesFilterCaptures", { n: counts.captures }) }] : []),
  ];

  // Whichever emptiness the user is actually looking at. Under "Everything" with a live transcript
  // running, the thing they are waiting for is the transcript, so that is the message that helps.
  const emptyMessage =
    filter === "captures"
      ? t("screenshotsEmpty")
      : filter === "notes" || !liveTranscript
        ? t("notesEmpty")
        : tr("liveTranscriptEmpty");

  return (
    <div style={{ display: "flex", flexDirection: "column", minHeight: 0, flex: variant === "window" ? 1 : undefined }}>
      {(actionSlot || capture || canSendTranscript) && (
        <div style={{ display: "flex", alignItems: "center", gap: 6, padding: "10px 14px 8px" }}>
          {/* Replaced in place by its own confirmation rather than confirming somewhere else: the user is
              looking at the button they just pressed, and the panel deliberately does not close or move
              focus to the chat - they are in a meeting. */}
          {canSendTranscript &&
            (transcriptSent ? (
              <span role="status" style={{ ...actionPill, ...sentPill }}>
                <IconCheck size={14} />
                {t("notesTranscriptSent")}
              </span>
            ) : (
              <button
                type="button"
                className="hub-tags-pill"
                title={t("notesUseInChatHint")}
                onClick={() => {
                  onTranscriptToChat?.();
                  setTranscriptSent(true);
                }}
                style={{ ...actionPill, border: "1px solid var(--hub-blue-soft-border)", color: "var(--hub-blue-text)", cursor: "pointer" }}
              >
                <IconChatArrow size={15} />
                {t("notesUseInChat")}
              </button>
            ))}
          {actionSlot}
          {capture && (
            <div style={{ marginLeft: "auto" }}>
              <CaptureControls
                captureAreaSet={capture.captureAreaSet}
                autoCapture={capture.autoCapture}
                onToggleAutoCapture={capture.onToggleAutoCapture}
                unavailableReason={capture.unavailableReason}
                onCapture={capture.onCapture}
                onChangeArea={capture.onChangeArea}
              />
            </div>
          )}
        </div>
      )}

      {!compact && (
        <div
          role="radiogroup"
          aria-label={t("notesFilterLabel")}
        style={{
          display: "flex",
          alignItems: "center",
          gap: 4,
          padding: "0 14px 8px",
          borderBottom: "1px solid var(--hub-divider)",
        }}
      >
        {chips.map((chip) => {
          const on = filter === chip.id;
          return (
            <button
              key={chip.id}
              type="button"
              role="radio"
              aria-checked={on}
              onClick={() => setFilter(chip.id)}
              className={on ? undefined : "hub-chip"}
              style={{
                border: "none",
                borderRadius: 7,
                padding: "4px 9px",
                fontSize: 12,
                fontWeight: on ? 600 : 400,
                cursor: "pointer",
                background: on ? "var(--hub-surface-hover)" : "transparent",
                color: on ? "var(--hub-text)" : "var(--hub-muted)",
              }}
            >
              {chip.label}
            </button>
          );
        })}
        <span style={{ marginLeft: "auto", fontSize: 10, fontWeight: 500, color: "var(--hub-placeholder)" }}>
            {filter === "notes" ? t("notesFilterNotesOnly") : filter === "captures" ? t("notesFilterCapturesOnly") : ""}
          </span>
        </div>
      )}

      {!compact && (
      <ul
        ref={streamRef}
        onScroll={onStreamScroll}
        role="list"
        data-testid="notes-stream"
        style={{
          margin: 0,
          padding: "10px 14px",
          listStyle: "none",
          display: "flex",
          flexDirection: "column",
          gap: 2,
          overflowY: "auto",
          // Fixed rather than a maximum, so the composer never moves under the user's hands as lines
          // arrive. In the detached window it simply takes whatever height is going spare.
          ...(v.streamHeight === null ? { flex: 1, minHeight: 0 } : { height: v.streamHeight }),
        }}
      >
        {items.length === 0 ? (
          <li data-testid="notes-stream-empty" style={{ fontSize: 12, color: "var(--hub-muted)" }}>
            {emptyMessage}
          </li>
        ) : (
          items.map((item) => {
            if (item.kind === "transcript")
              return (
                <TranscriptRow
                  key={item.id}
                  segment={item.segment}
                  showSpeaker={item.showSpeaker}
                  stampColumnPx={stampPx}
                  onPin={pin}
                />
              );
            if (item.kind === "note")
              return (
                <NoteRow
                  key={item.id}
                  note={item.note}
                  stampColumnPx={stampPx}
                  onEdit={onEdit}
                  onDelete={onDelete}
                />
              );
            return (
              <CaptureRow
                key={item.id}
                shot={item.shot}
                previewUrl={previews.get(item.shot.id) ?? ""}
                stampColumnPx={stampPx}
                thumbWidth={v.thumbWidth}
                thumbHeight={v.thumbHeight}
                onDelete={onDeleteShot}
                liveRecordingId={liveRecordingId}
                onToChat={
                  onShotToChat
                    ? (id) => {
                        onShotToChat(id);
                        setCaptureSent(true);
                      }
                    : undefined
                }
              />
            );
          })
        )}
      </ul>
      )}

      <div
        style={{
          display: "flex",
          flexDirection: "column",
          gap: 7,
          padding: "8px 14px 14px",
          borderTop: "1px solid var(--hub-divider)",
        }}
      >
        {liveTranscript && (
          <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
            <span
              aria-hidden
              style={{
                width: 6,
                height: 6,
                borderRadius: "50%",
                background: "var(--hub-green)",
                animation: "blink 1.6s infinite",
                flexShrink: 0,
              }}
            />
            <span
              data-testid="live-transcript-status"
              // Not role="alert": falling behind is not an error, and announcing it as one would
              // interrupt whatever the user is doing to tell them about something that fixes itself.
              aria-live="polite"
              // The short string fits an 11px single-line row that shares its space with a
              // confirmation; the long one - which is the sentence that says the text is not final -
              // survives on hover rather than being lost.
              title={status.long}
              style={{ fontSize: 11, color: "var(--hub-muted)" }}
            >
              {status.short}
            </span>
            <span style={{ marginLeft: "auto", display: "flex", alignItems: "center", gap: 8 }}>
              {captureSent && (
                <span role="status" style={{ fontSize: 11, fontWeight: 600, color: "var(--hub-green-text)" }}>
                  {t("notesCaptureSent")}
                </span>
              )}
              {statusSlot}
            </span>
          </div>
        )}

        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: 8,
            background: "var(--hub-surface)",
            border: "1px solid var(--hub-blue-soft-border)",
            borderRadius: 10,
            padding: "7px 9px",
          }}
        >
          {pinnedAtMs === null ? (
            <span data-testid="composer-stamp" style={stampBadge}>
              {formatDuration(elapsedMs)}
            </span>
          ) : (
            // A button once pinned, because there has to be a way back to the clock that does not
            // involve filing a note you did not want. Escape is not available: HubPopover closes on it.
            <button
              type="button"
              data-testid="composer-stamp"
              aria-pressed
              aria-label={t("notesUnpin")}
              title={t("notesUnpin")}
              onClick={() => setPinnedAtMs(null)}
              style={{ ...stampBadge, border: "none", cursor: "pointer" }}
            >
              {formatDuration(pinnedAtMs)}
            </button>
          )}
          <input
            ref={inputRef}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key !== "Enter") return;
              e.preventDefault();
              file();
            }}
            placeholder={t("notesComposerPlaceholder")}
            aria-label={t("notesComposerPlaceholder")}
            disabled={disabled}
            autoFocus={v.autoFocus}
            style={{
              minWidth: 0,
              flex: 1,
              border: "none",
              background: "transparent",
              outline: "none",
              fontSize: v.inputFontSize,
              color: "var(--hub-text)",
            }}
          />
          <span
            aria-hidden
            title={t("notesEnterHint")}
            style={{
              flexShrink: 0,
              fontFamily: "ui-monospace, Menlo, monospace",
              fontSize: 10,
              color: "var(--hub-muted-2)",
              border: "1px solid var(--hub-field-border)",
              borderRadius: 4,
              padding: "1px 4px",
            }}
          >
            {"⏎"}
          </span>
        </div>

        {/* Built from what the shell actually registered rather than from a literal, so the copy cannot
            be wrong about a hotkey the user has changed - which is the one thing a hardcoded line here
            would eventually be. Hidden entirely in a plain browser, which holds no global keys. */}
        {hotkeyHint && (
          <p data-testid="notes-hotkey-hint" style={{ margin: 0, fontSize: 10, color: "var(--hub-placeholder)" }}>
            {hotkeyHint}
          </p>
        )}
      </div>
    </div>
  );
}

const actionPill: React.CSSProperties = {
  display: "inline-flex",
  alignItems: "center",
  gap: 7,
  height: 30,
  padding: "0 11px",
  borderRadius: 9,
  fontSize: 12,
  fontWeight: 600,
  background: "var(--hub-blue-soft-bg)",
  border: "1px solid var(--hub-blue-soft-border)",
  color: "var(--hub-blue-text)",
};

const sentPill: React.CSSProperties = {
  background: "var(--hub-green-soft-bg)",
  border: "1px solid var(--hub-green-soft-border)",
  color: "var(--hub-green-text)",
};

const stampBadge: React.CSSProperties = {
  flexShrink: 0,
  fontFamily: "ui-monospace, Menlo, monospace",
  fontSize: 11,
  fontWeight: 600,
  color: "var(--hub-blue-text)",
  background: "var(--hub-blue-soft-bg)",
  borderRadius: 5,
  padding: "2px 5px",
};
