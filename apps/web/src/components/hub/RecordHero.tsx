import { useTranslation } from "react-i18next";
import HubLevelMeter from "./HubLevelMeter";

// Transport glyphs for the recording pill's circular buttons (16x16, drawn in `currentColor`). Pause is two
// bars, stop a filled rounded square - Feather/Lucide-style, matching the app's existing transport icons.
const IconPause = () => (
  <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false">
    <rect x="4.5" y="3.5" width="2.5" height="9" rx="0.75" fill="currentColor" />
    <rect x="9" y="3.5" width="2.5" height="9" rx="0.75" fill="currentColor" />
  </svg>
);

const IconStop = () => (
  <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false">
    <rect x="4" y="4" width="8" height="8" rx="1" fill="currentColor" />
  </svg>
);

// A 36px circular transport button (pause / stop) used inside the recording pill.
function CircleButton({
  onClick,
  label,
  background,
  hoverBackground,
  color,
  shadow,
  children,
}: {
  onClick: () => void;
  label: string;
  background: string;
  hoverBackground: string;
  color: string;
  shadow?: string;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      aria-label={label}
      style={{
        width: 36,
        height: 36,
        flexShrink: 0,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        borderRadius: "50%",
        border: "none",
        cursor: "pointer",
        background,
        color,
        boxShadow: shadow,
      }}
      onMouseEnter={(e) => (e.currentTarget.style.background = hoverBackground)}
      onMouseLeave={(e) => (e.currentTarget.style.background = background)}
    >
      {children}
    </button>
  );
}

export type RecordHeroProps = {
  /** Whether a recording is in progress (drives the idle↔recording pill swap). */
  recording: boolean;
  /** Whether the in-progress recording is paused (meter hidden, pause button becomes resume). */
  paused: boolean;
  /** Formatted MM:SS elapsed (recorded) time, shown in the recording pill. */
  mmss: string;
  /** The live recording MediaStream, tapped by the level meter while recording. */
  stream: MediaStream | null;
  /** Whether the user may record in the current room (gates the idle pill). */
  canRecord: boolean;
  /** True while an upload is in flight (also disables the idle pill). */
  busy: boolean;
  /** Overrides the busy title/aria-label (e.g. to show screenshot-attach progress) - falls back to the
   * generic "Uploading…" when unset, so existing callers are unaffected. */
  busyLabel?: string;
  /** True when the current source selection can't start a recording (e.g. "No microphone" + no system audio). */
  startDisabled: boolean;
  onStart: () => void;
  onPause: () => void;
  onResume: () => void;
  onStop: () => void;
  /** Bubbles the meter's debounced silence state up so the parent can show the "no sound" hint. */
  onSilentChange: (silent: boolean) => void;
};

// The record-centric "hero" of the command hub: an idle pill that morphs into the live recording pill
// (timer + level meter + pause + stop) in place. Purely presentational - all state and handlers come from
// the Recorder. The accessible names record / pause / resume / stop are preserved for the recorder tests.
export default function RecordHero({
  recording,
  paused,
  mmss,
  stream,
  canRecord,
  busy,
  busyLabel,
  startDisabled,
  onStart,
  onPause,
  onResume,
  onStop,
  onSilentChange,
}: RecordHeroProps) {
  const { t } = useTranslation("workspace");

  if (recording) {
    return (
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 14,
          height: 52,
          borderRadius: 26,
          padding: "0 8px 0 16px",
          background: "var(--hub-red-soft-bg)",
          border: "1px solid var(--hub-red-soft-border)",
        }}
      >
        {/* Blinking record dot. */}
        <span
          aria-hidden
          style={{
            width: 9,
            height: 9,
            borderRadius: "50%",
            background: "#ff5b60",
            animation: "blink 1.2s infinite",
          }}
        />
        {/* Timer (MM:SS). Kept discoverable by the "●"-free recording signal via role/name in tests. */}
        <span
          style={{
            fontFamily: "ui-monospace, Menlo, monospace",
            fontWeight: 600,
            fontSize: 18,
            color: "var(--hub-red-text)",
            fontVariantNumeric: "tabular-nums",
          }}
        >
          {mmss}
        </span>
        {/* Meter only runs while actively capturing; when paused we show the Paused label in its place. */}
        {paused ? (
          <span style={{ fontSize: 13, fontWeight: 500, color: "var(--hub-red-text)" }}>
            {t("recPaused")}
          </span>
        ) : (
          <HubLevelMeter stream={stream} onSilentChange={onSilentChange} />
        )}
        <CircleButton
          onClick={paused ? onResume : onPause}
          label={paused ? t("recResume") : t("recPause")}
          background="var(--hub-surface-hover)"
          hoverBackground="var(--hub-border-hover)"
          color="var(--hub-text)"
        >
          {paused ? (
            <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false">
              <path d="M5.5 3.4l6.5 4.6-6.5 4.6z" fill="currentColor" />
            </svg>
          ) : (
            <IconPause />
          )}
        </CircleButton>
        <CircleButton
          onClick={onStop}
          label={t("recStop")}
          background="var(--hub-red)"
          hoverBackground="var(--hub-red-hover)"
          color="#fff"
          shadow="0 3px 12px rgba(229,72,77,.5)"
        >
          <IconStop />
        </CircleButton>
      </div>
    );
  }

  const disabled = busy || startDisabled || !canRecord;
  const uploadingLabel = busyLabel ?? t("recUploading");
  const title = !canRecord ? t("recNoPermission") : busy ? uploadingLabel : t("recRecord");

  return (
    <button
      type="button"
      onClick={onStart}
      disabled={disabled}
      title={title}
      aria-label={busy ? uploadingLabel : t("recRecord")}
      style={{
        display: "flex",
        alignItems: "center",
        gap: 13,
        height: 52,
        borderRadius: 26,
        padding: "0 8px",
        background: "var(--hub-surface)",
        border: "1px solid var(--hub-border)",
        cursor: disabled ? "not-allowed" : "pointer",
        opacity: disabled ? 0.5 : 1,
      }}
      onMouseEnter={(e) => {
        if (!disabled) e.currentTarget.style.borderColor = "var(--hub-border-hover)";
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.borderColor = "var(--hub-border)";
      }}
    >
      {/* Red circle with a white dot. */}
      <span
        aria-hidden
        style={{
          width: 36,
          height: 36,
          flexShrink: 0,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          borderRadius: "50%",
          background: "var(--hub-red)",
          boxShadow: "0 3px 12px rgba(229,72,77,.5)",
        }}
      >
        <span style={{ width: 13, height: 13, borderRadius: "50%", background: "#fff" }} />
      </span>
      {/* The "Start recording" label collapses at narrow widths, leaving just the red circle button; the
          button's aria-label keeps the accessible name "Record" in every layout. */}
      <span
        className="hidden md:inline"
        style={{
          fontFamily: "system-ui",
          fontWeight: 600,
          fontSize: 16,
          color: "var(--hub-text)",
          paddingRight: 14,
        }}
      >
        {t("recStartRecording")}
      </span>
    </button>
  );
}
