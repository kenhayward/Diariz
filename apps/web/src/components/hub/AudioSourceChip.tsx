import { useTranslation } from "react-i18next";

// Mic + chevron glyphs (Feather/Lucide-style, 18px, stroke `currentColor` unless overridden). The mic gets
// a blue accent; the chevron the muted tone.
const IconMic = () => (
  <svg viewBox="0 0 24 24" width="18" height="18" fill="none" aria-hidden="true" focusable="false"
    stroke="var(--hub-blue-text)" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <rect x="9" y="2" width="6" height="12" rx="3" />
    <path d="M5 10a7 7 0 0 0 14 0M12 17v4M8 21h8" />
  </svg>
);

const IconChevron = () => (
  <svg viewBox="0 0 24 24" width="16" height="16" fill="none" aria-hidden="true" focusable="false"
    stroke="var(--hub-muted)" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M6 9l6 6 6-6" />
  </svg>
);

/**
 * The single "Audio source" chip that opens the Audio source popover. Replaces the old inline mic select +
 * system-audio checkbox + settings cog. Shows a green "+System" pill when system audio is on. Purely a
 * trigger - all backing state lives in the Recorder.
 */
export default function AudioSourceChip({
  systemAudio,
  expanded,
  disabled,
  recording = false,
  onClick,
}: {
  systemAudio: boolean;
  expanded: boolean;
  disabled?: boolean;
  /**
   * Whether a recording is running. Not a look - the chip is identical either way - but a measure of how
   * much room it has: the recording cluster is ~290px wider than the idle one, so both of the chip's
   * collapse steps have to come earlier. Separate from `disabled` (which the recorder also derives from
   * the recording state) because these are two different reasons to care about the same fact.
   */
  recording?: boolean;
  onClick: () => void;
}) {
  const { t } = useTranslation("workspace");
  // Measured requirements, as bar container widths: everything on needs 725px while recording against
  // 575px idle, and mic + pill + chevron needs 531px against 343px. Each threshold sits just above the
  // requirement of the step above it, so there is no width at which the wider layout is still rendering
  // and no longer fits. Written out in full because Tailwind only generates class names it can see whole.
  const labelStep = recording ? "@min-[740px]:inline" : "@xl:inline";
  const chromeStep = recording ? "@max-[560px]:hidden" : "@max-[400px]:hidden";
  const dotStep = recording ? "@max-[560px]:block" : "@max-[400px]:block";
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={t("audioSourceChip")}
      aria-haspopup="dialog"
      aria-expanded={expanded}
      style={{
        display: "flex",
        alignItems: "center",
        gap: 10,
        height: 44,
        borderRadius: 11,
        padding: "0 14px",
        background: "var(--hub-surface)",
        border: "1px solid var(--hub-border)",
        color: "var(--hub-text)",
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
      <IconMic />
      {/* The text label collapses when there is no room for it. The measure is the *capture bar's* width,
          not the window's: the bar spans `window - left panel - chat panel`, so a wide window with the chat
          panel dragged out leaves a narrow bar, and a viewport breakpoint (`md:`) would keep the label
          showing while the cluster spilled over the chat panel. `@xl` asks the bar (its `@container`)
          instead. The mic icon (+ "+System" pill + chevron) still carry the chip at any width, and the
          button's aria-label keeps the accessible name "Audio source" in every layout. */}
      <span
        className={`hidden min-w-0 truncate ${labelStep}`}
        style={{ fontFamily: "system-ui", fontWeight: 500, fontSize: 14.5, color: "var(--hub-text)" }}
      >
        {t("audioSourceChip")}
      </span>
      {/* Second, tighter step below the label one. With the label gone, "+System" is 72px of the 144px the
          chip then occupies - the biggest thing left to shed - and the chevron another 26px. Below a 400px
          bar the pill becomes a green dot and the chevron goes, leaving a 44px icon chip. The threshold is
          the idle one in both states: the chip looks the same whether or not a recording is running. */}
      {systemAudio && (
        <>
          <span
            className={chromeStep}
            style={{
              fontFamily: "system-ui",
              fontWeight: 500,
              fontSize: 12,
              color: "var(--hub-green-text)",
              background: "var(--hub-green-soft-bg)",
              padding: "2px 7px",
              borderRadius: 6,
            }}
          >
            +System
          </span>
          {/* Stands in for the pill, so it appears exactly when the pill would have. No accessible text of
              its own: the chip's aria-label names the control and the popover states the source. */}
          <span
            data-testid="system-audio-dot"
            aria-hidden
            className={`hidden shrink-0 ${dotStep}`}
            style={{ width: 8, height: 8, borderRadius: "50%", background: "var(--hub-green-text)" }}
          />
        </>
      )}
      <span data-testid="source-chevron" className={`flex ${chromeStep}`}>
        <IconChevron />
      </span>
    </button>
  );
}
