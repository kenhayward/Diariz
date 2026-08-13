import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import NotesSection from "../components/NotesSection";
import ShotStrip from "../components/hub/ShotStrip";
import { createNotesClient, type NotesClient, type NotesState } from "../lib/notesChannel";

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

  const smallButton = {
    fontFamily: "system-ui",
    fontWeight: 500,
    fontSize: 12,
    padding: "2px 6px",
    borderRadius: 6,
    border: "1px solid var(--hub-border)",
    background: "transparent",
    color: "var(--hub-text-2)",
  } as const;

  return (
    <div
      style={{
        padding: 16,
        display: "flex",
        flexDirection: "column",
        gap: 12,
        height: "100vh",
        boxSizing: "border-box",
        background: "var(--hub-popover-bg)",
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
        <span
          aria-hidden
          style={{ width: 9, height: 9, borderRadius: "50%", background: "var(--hub-red)", animation: "blink 1.2s infinite" }}
        />
        <span style={{ fontFamily: "system-ui", fontWeight: 700, fontSize: 17, color: "var(--hub-text)" }}>
          {t("liveNotesTitle")}
        </span>
      </div>

      {state === null && (
        <p style={{ margin: 0, fontFamily: "system-ui", fontSize: 13, color: "var(--hub-muted)" }}>
          {t("notesPopoutWaiting")}
        </p>
      )}

      {/* Only once contact actually existed. A window opened before its host is ready has not *lost*
          anything, and telling the user to "bring it back" when it was never there is just wrong. */}
      {lost && state !== null && (
        <p role="status" style={{ margin: 0, fontFamily: "system-ui", fontSize: 13, color: "var(--hub-red-text)" }}>
          {t("notesPopoutDisconnected")}
        </p>
      )}

      {state !== null && (
        <div style={{ flex: 1, overflowY: "auto" }}>
          <NotesSection
            notes={state.lines}
            onAdd={(text) => client?.add(text)}
            onEdit={(id, text) => client?.edit(id, text)}
            onDelete={(id) => client?.remove(id)}
            // Disabled rather than hidden: a note typed into a dead channel must not look accepted,
            // but the box vanishing would read as the notes themselves having gone.
            disabled={!live}
          />
        </div>
      )}

      {state?.canCapture && (
        <div style={{ borderTop: "1px solid var(--hub-border)", paddingTop: 8, display: "flex", flexDirection: "column", gap: 6 }}>
          <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
            <span style={{ fontFamily: "system-ui", fontWeight: 600, fontSize: 12, color: "var(--hub-text-2)" }}>
              {t("screenshots")} ({state.shots.length})
            </span>
            <div style={{ display: "flex", gap: 6 }}>
              <button
                type="button"
                onClick={() => client?.capture()}
                // Same gate as the popover's capture button: with no area chosen this opens the picker
                // and then sits inert until it settles, which reads as the window having frozen.
                disabled={!live || !state.captureAreaSet}
                title={state.captureAreaSet ? undefined : t("screenshotCaptureNeedsArea")}
                style={{ ...smallButton, cursor: live && state.captureAreaSet ? "pointer" : "not-allowed" }}
              >
                {t("screenshotCaptureButton")}
              </button>
              <button
                type="button"
                onClick={() => client?.changeArea()}
                disabled={!live}
                style={{ ...smallButton, cursor: live ? "pointer" : "not-allowed" }}
              >
                {t("screenshotCaptureArea")}
              </button>
            </div>
          </div>
          <ShotStrip shots={state.shots} onDelete={(id) => client?.removeShot(id)} />
        </div>
      )}
    </div>
  );
}
