import { type ChangeEvent, type FocusEvent } from "react";
import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import { formatSourceToken, type AudioConstraints, type SourceOption, type SourceSelection } from "../../lib/audioDevices";

// Section header shared by the popover's groups (uppercase, tracked, muted).
function SectionHeader({ children }: { children: string }) {
  return (
    <div
      style={{
        fontFamily: "system-ui",
        fontWeight: 600,
        fontSize: 11,
        letterSpacing: ".09em",
        textTransform: "uppercase",
        color: "var(--hub-muted)",
      }}
    >
      {children}
    </div>
  );
}

const IconChevron = () => (
  <svg viewBox="0 0 24 24" width="16" height="16" fill="none" aria-hidden="true" focusable="false"
    stroke="var(--hub-muted)" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M6 9l6 6 6-6" />
  </svg>
);

const IconCheck = () => (
  <svg viewBox="0 0 24 24" width="13" height="13" fill="none" aria-hidden="true" focusable="false"
    stroke="currentColor" strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round">
    <path d="M5 12l5 5 9-11" />
  </svg>
);

// One processing flag, rendered as a chip. The real checkbox is present (so `getByRole("checkbox")` and its
// accessible name survive) but visually hidden; the styled label is the chip. Active chips are blue-tinted
// with a check; inactive are outlined + muted. Disabled when there's no mic to tune.
function ProcessingChip({
  label,
  active,
  disabled,
  onToggle,
}: {
  label: string;
  active: boolean;
  disabled: boolean;
  onToggle: () => void;
}) {
  return (
    <label
      style={{
        display: "inline-flex",
        alignItems: "center",
        gap: 6,
        padding: "8px 11px",
        borderRadius: 9,
        fontFamily: "system-ui",
        fontSize: 13,
        fontWeight: 500,
        cursor: disabled ? "not-allowed" : "pointer",
        opacity: disabled ? 0.5 : 1,
        background: active ? "var(--hub-blue-soft-bg)" : "transparent",
        border: `1px solid ${active ? "var(--hub-blue-soft-border)" : "var(--hub-border)"}`,
        color: active ? "var(--hub-blue-text)" : "var(--hub-muted)",
      }}
    >
      <input
        type="checkbox"
        checked={active}
        disabled={disabled}
        onChange={onToggle}
        aria-label={label}
        style={{ position: "absolute", width: 1, height: 1, padding: 0, margin: -1, overflow: "hidden", clip: "rect(0 0 0 0)" }}
      />
      {active && <IconCheck />}
      <span>{label}</span>
    </label>
  );
}

export type AudioSourcePopoverProps = {
  open: boolean;
  onClose: () => void;
  /** Current source selection (its token drives the select value). */
  selection: SourceSelection;
  /** Pre-built mic options (label already translated). */
  options: SourceOption[];
  onSelectSource: (e: ChangeEvent<HTMLSelectElement>) => void;
  /** Unlock device labels on first focus of the select (the natural permission prompt). */
  onFocusSelect: (e: FocusEvent<HTMLSelectElement>) => void;
  /** Recording in progress - lock the source controls. */
  recording: boolean;
  /** Whether this environment can capture system audio (hides the toggle row when false). */
  canSystemAudio: boolean;
  systemAudio: boolean;
  onToggleSystemAudio: (on: boolean) => void;
  constraints: AudioConstraints;
  onToggleConstraint: (key: keyof AudioConstraints) => void;
};

/**
 * The Audio source popover: mic select, a "capture system audio" toggle, and the four processing flags as
 * chips. Opened from the AudioSourceChip. Keeps the same `microphone` combobox, `system audio` checkbox and
 * four processing checkbox accessible names as the old inline controls so behaviour + tests are unchanged.
 */
export default function AudioSourcePopover({
  open,
  onClose,
  selection,
  options,
  onSelectSource,
  onFocusSelect,
  recording,
  canSystemAudio,
  systemAudio,
  onToggleSystemAudio,
  constraints,
  onToggleConstraint,
}: AudioSourcePopoverProps) {
  const { t } = useTranslation("workspace");
  const noMic = selection.kind === "none";
  const selectValue = formatSourceToken(selection);

  const processing: [keyof AudioConstraints, string][] = [
    ["echoCancellation", t("constraintEcho")],
    ["noiseSuppression", t("constraintNoise")],
    ["autoGainControl", t("constraintAgc")],
    ["mono", t("constraintMono")],
  ];

  return (
    <HubPopover open={open} onClose={onClose} width={352} anchorClassName="right-0" ariaLabel={t("audioSourceChip")}>
      <div data-testid="audio-source-popover" style={{ padding: 16, display: "flex", flexDirection: "column", gap: 14 }}>
        {/* Microphone */}
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <SectionHeader>{t("audioSourceMicHeader")}</SectionHeader>
          <div style={{ position: "relative", display: "flex", alignItems: "center" }}>
            <select
              value={selectValue}
              onChange={onSelectSource}
              onFocus={onFocusSelect}
              disabled={recording}
              aria-label={t("sourceMicrophone")}
              style={{
                width: "100%",
                height: 44,
                borderRadius: 9,
                padding: "0 34px 0 12px",
                background: "var(--hub-surface)",
                border: "1px solid var(--hub-border)",
                color: "var(--hub-text)",
                fontFamily: "system-ui",
                fontSize: 14.5,
                appearance: "none",
                WebkitAppearance: "none",
                MozAppearance: "none",
                cursor: recording ? "not-allowed" : "pointer",
              }}
            >
              {options.map((o) => (
                <option key={o.token} value={o.token}>
                  {o.label}
                </option>
              ))}
            </select>
            <span style={{ position: "absolute", right: 12, pointerEvents: "none" }}>
              <IconChevron />
            </span>
          </div>
        </div>

        {/* Capture system audio */}
        {canSystemAudio && (
          <label
            style={{
              display: "flex",
              alignItems: "center",
              gap: 10,
              padding: "10px 12px",
              borderRadius: 9,
              background: "var(--hub-green-soft-bg)",
              border: "1px solid var(--hub-green-soft-border)",
              color: "var(--hub-text)",
              fontFamily: "system-ui",
              fontSize: 14.5,
              fontWeight: 500,
              cursor: recording ? "not-allowed" : "pointer",
              opacity: recording ? 0.6 : 1,
            }}
          >
            <span
              aria-hidden
              style={{
                width: 20,
                height: 20,
                flexShrink: 0,
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                borderRadius: 6,
                background: systemAudio ? "var(--hub-green)" : "transparent",
                border: `1px solid ${systemAudio ? "var(--hub-green)" : "var(--hub-green-soft-border)"}`,
                color: "#0e1729",
              }}
            >
              {systemAudio && <IconCheck />}
            </span>
            <input
              type="checkbox"
              checked={systemAudio}
              disabled={recording}
              onChange={(e) => onToggleSystemAudio(e.target.checked)}
              aria-label={t("systemAudioToggle")}
              style={{ position: "absolute", width: 1, height: 1, padding: 0, margin: -1, overflow: "hidden", clip: "rect(0 0 0 0)" }}
            />
            {t("captureSystemAudio")}
          </label>
        )}

        {/* Processing */}
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <SectionHeader>{t("audioProcessingHeader")}</SectionHeader>
          <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
            {processing.map(([key, label]) => (
              <ProcessingChip
                key={key}
                label={label}
                active={constraints[key]}
                disabled={recording || noMic}
                onToggle={() => onToggleConstraint(key)}
              />
            ))}
          </div>
        </div>

        {/* Footer note */}
        <p
          style={{
            margin: 0,
            paddingTop: 12,
            borderTop: "1px solid var(--hub-border)",
            fontFamily: "system-ui",
            fontWeight: 400,
            fontSize: 12,
            color: "var(--hub-muted-2)",
          }}
        >
          {t("processingMicOnlyNote")}
        </p>
      </div>
    </HubPopover>
  );
}
