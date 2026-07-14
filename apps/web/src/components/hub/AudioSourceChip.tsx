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
  onClick,
}: {
  systemAudio: boolean;
  expanded: boolean;
  disabled?: boolean;
  onClick: () => void;
}) {
  const { t } = useTranslation("workspace");
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
      {/* The text label collapses at narrow widths - the mic icon (+ "+System" pill + chevron) carry the
          chip; the button's aria-label keeps the accessible name "Audio source" in every layout. */}
      <span
        className="hidden md:inline"
        style={{ fontFamily: "system-ui", fontWeight: 500, fontSize: 14.5, color: "var(--hub-text)" }}
      >
        {t("audioSourceChip")}
      </span>
      {systemAudio && (
        <span
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
      )}
      <IconChevron />
    </button>
  );
}
