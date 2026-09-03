import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import LiveNotesStream from "../components/hub/LiveNotesStream";
import { createNotesClient, type NotesClient, type NotesState } from "../lib/notesChannel";
import { formatDuration } from "../lib/format";

/**
 * The detached live-notes window, loaded by the desktop shell at /notes-popout so notes can be taken
 * while a call has the screen.
 *
 * It owns nothing. Every line it shows came from the main window, and every edit goes back there to be
 * applied - including the timestamp, which only the host can produce because only the host knows the
 * recorded (pause-aware) clock. It never calls the API, which is also why it needs no auth: with no
 * host answering on the channel it simply renders the waiting state.
 */
export default function NotesPopout() {
  const { t } = useTranslation("workspace");
  const [state, setState] = useState<NotesState | null>(null);
  const [lost, setLost] = useState(false);
  const clientRef = useRef<NotesClient | null>(null);

  useEffect(() => {
    const client = createNotesClient({
      onState: (s) => {
        setState(s);
        setLost(false);
      },
      onEnded: () => window.close(),
      onDisconnected: () => setLost(true),
    });
    clientRef.current = client;

    // The shell also reports the closed window to the host over IPC, so this is the fast path rather
    // than the only one - but it brings the inline popover back the moment the window goes.
    const bye = () => client.close();
    window.addEventListener("pagehide", bye);
    return () => {
      window.removeEventListener("pagehide", bye);
      client.dispose();
    };
  }, []);

  const client = clientRef.current;
  const live = state !== null && !lost;
  const elapsedMs = useTickingClock(state?.clock);

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        height: "100vh",
        overflow: "hidden",
        boxSizing: "border-box",
        background: "var(--hub-popover-bg)",
      }}
    >
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 8,
          height: 40,
          flexShrink: 0,
          padding: "8px 10px 8px 12px",
          boxSizing: "border-box",
          background: "var(--hub-bar-bg)",
          borderBottom: "1px solid var(--hub-bar-border-bottom)",
        }}
      >
        <span
          aria-hidden
          style={{ width: 9, height: 9, borderRadius: "50%", background: "var(--hub-red)", animation: "blink 1.2s infinite" }}
        />
        <span style={{ fontFamily: "system-ui", fontWeight: 600, fontSize: 12, color: "var(--hub-text-2)" }}>
          {t("notesPopoutTitle")}
        </span>
        <span
          data-testid="notes-elapsed"
          style={{ fontFamily: "ui-monospace, Menlo, monospace", fontSize: 12, color: "var(--hub-muted)" }}
        >
          {formatDuration(elapsedMs)}
        </span>
        {/* On top and Compact land here. */}
      </div>

      {state === null && (
        <p style={{ margin: 0, padding: 16, fontFamily: "system-ui", fontSize: 13, color: "var(--hub-muted)" }}>
          {t("notesPopoutWaiting")}
        </p>
      )}

      {/* Only once contact actually existed. A window opened before its host is ready has not *lost*
          anything, and telling the user to "bring it back" when it was never there is just wrong. */}
      {lost && state !== null && (
        <p
          role="status"
          style={{ margin: 0, padding: "10px 14px 0", fontFamily: "system-ui", fontSize: 13, color: "var(--hub-red-text)" }}
        >
          {t("notesPopoutDisconnected")}
        </p>
      )}

      {state !== null && (
        <LiveNotesStream
          variant="window"
          lines={state.lines}
          shots={state.shots}
          elapsedMs={elapsedMs}
          liveTranscript={state.liveTranscript}
          liveLagSeconds={state.liveLagSeconds}
          liveDegraded={state.liveDegraded}
          onAdd={(text, atMs) => client?.add(text, atMs)}
          onEdit={(id, text) => client?.edit(id, text)}
          onDelete={(id) => client?.remove(id)}
          onDeleteShot={(id) => client?.removeShot(id)}
          // Relayed, never done here. `chatAttachments` is an in-TAB pub/sub and the chat panel lives
          // in the main window, so publishing from this one would reach no subscribers and silently do
          // nothing - which is why this file must not import that module at all
          // (asserted in bundleBoundary.test.ts).
          onTranscriptToChat={live ? () => client?.transcriptToChat() : undefined}
          onShotToChat={live ? (id) => client?.shotToChat(id) : undefined}
          liveRecordingId={state.liveRecordingId}
          // Disabled rather than hidden: a note typed into a dead channel must not look accepted, but
          // the box vanishing would read as the notes themselves having gone.
          disabled={!live}
          capture={
            state.canCapture
              ? {
                  captureAreaSet: state.captureAreaSet,
                  autoCapture: state.autoCapture,
                  onToggleAutoCapture: state.canAutoCapture ? () => client?.toggleAutoCapture() : undefined,
                  // A dead channel means a click would travel nowhere. Icon-only buttons cannot be
                  // silently greyed out, so the row is told why - the banner above says the same thing
                  // at length.
                  unavailableReason: live ? undefined : t("notesPopoutOffline"),
                  onCapture: () => client?.capture(),
                  onChangeArea: () => client?.changeArea(),
                }
              : undefined
          }
        />
      )}
    </div>
  );
}

/// The recorded clock, ticked here rather than pushed across the channel.
///
/// The host sends a reading and the wall-clock moment it was taken; this extrapolates between them once
/// a second. That is the whole reason the state carries `{ recordedMs, atWallMs, running }` rather than
/// a ticking number: the host republishes its entire state - every capture's thumbnail blob included -
/// whenever anything in it changes, and once the main window is hidden to the tray Chromium throttles
/// its timers to roughly 1 Hz anyway, so a clock driven by publishes would stutter exactly when this
/// window is the one being used. Frozen while `running` is false, so a paused meeting stops counting.
function useTickingClock(clock: NotesState["clock"]): number {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!clock?.running) return;
    const id = setInterval(() => setNow(Date.now()), 1_000);
    return () => clearInterval(id);
  }, [clock?.running, clock?.atWallMs]);

  // Re-read the moment a fresh publish lands, so resuming does not leave a second of stale time on
  // screen while waiting for the next tick.
  useEffect(() => setNow(Date.now()), [clock?.atWallMs, clock?.recordedMs]);

  if (!clock) return 0;
  return clock.running ? clock.recordedMs + Math.max(0, now - clock.atWallMs) : clock.recordedMs;
}
