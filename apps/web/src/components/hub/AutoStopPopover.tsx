import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import type { AutoStopChoice } from "../../lib/recorderSchedule";

// Section header shared with the other hub popovers (uppercase, tracked, muted).
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
        padding: "6px 11px",
      }}
    >
      {children}
    </div>
  );
}

const IconCheck = () => (
  <svg viewBox="0 0 24 24" width="14" height="14" fill="none" aria-hidden="true" focusable="false"
    stroke="var(--hub-blue)" strokeWidth={2.5} strokeLinecap="round" strokeLinejoin="round">
    <path d="M5 12l5 5 9-11" />
  </svg>
);

// One selectable option row. Selected rows are blue-tinted with a trailing check; unselected rows are muted
// and highlight on hover. `topBordered` sets off the "at a set time" row + tints its text blue.
function OptionRow({
  label,
  selected,
  topBordered,
  onSelect,
}: {
  label: string;
  selected: boolean;
  topBordered?: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      style={{
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        width: "100%",
        padding: 11,
        borderRadius: 9,
        border: "none",
        background: selected ? "var(--hub-blue-soft-bg)" : "transparent",
        color: selected ? "var(--hub-text)" : topBordered ? "var(--hub-blue-text)" : "var(--hub-text-2)",
        fontFamily: "system-ui",
        fontWeight: 500,
        fontSize: 14.5,
        textAlign: "left",
        cursor: "pointer",
        ...(topBordered ? { marginTop: 4, borderTop: "1px solid var(--hub-border)", borderRadius: 0 } : {}),
      }}
      onMouseEnter={(e) => {
        if (!selected) e.currentTarget.style.background = "var(--hub-surface-hover)";
      }}
      onMouseLeave={(e) => {
        if (!selected) e.currentTarget.style.background = "transparent";
      }}
    >
      <span>{label}</span>
      {selected && <IconCheck />}
    </button>
  );
}

export type AutoStopPopoverProps = {
  open: boolean;
  onClose: () => void;
  choice: AutoStopChoice;
  time: string;
  onChoice: (choice: AutoStopChoice) => void;
  onTime: (time: string) => void;
  /** Formatted "stops at HH:MM" hint (rendered inside the popover when a stop is scheduled). */
  scheduledHint?: string | null;
};

/**
 * The Auto-stop popover: option rows that schedule the current recording to end after a fixed interval or at
 * a set clock time. Wired to the exact same `autoStopChoice` / `autoStopTime` state (and thus the same
 * persistence, watcher and "stops at" display) as the old inline <select>. Opened from the clock icon button.
 */
export default function AutoStopPopover({
  open,
  onClose,
  choice,
  time,
  onChoice,
  onTime,
  scheduledHint,
}: AutoStopPopoverProps) {
  const { t } = useTranslation("workspace");

  const relative: [AutoStopChoice, string][] = [
    ["off", t("autoStopOff")],
    ["in15", t("autoStopIn15")],
    ["in30", t("autoStopIn30")],
    ["in60", t("autoStopIn60")],
  ];

  return (
    <HubPopover open={open} onClose={onClose} width={280} anchorClassName="right-0" ariaLabel={t("autoStopLabel")}>
      <div data-testid="auto-stop-popover" style={{ padding: 10, display: "flex", flexDirection: "column" }}>
        <SectionHeader>{t("autoStopLabel")}</SectionHeader>
        {relative.map(([value, label]) => (
          <OptionRow key={value} label={label} selected={choice === value} onSelect={() => onChoice(value)} />
        ))}
        <OptionRow
          label={t("autoStopAt")}
          selected={choice === "at"}
          topBordered
          onSelect={() => onChoice("at")}
        />
        {choice === "at" && (
          <input
            type="time"
            value={time}
            onChange={(e) => onTime(e.target.value)}
            aria-label={t("autoStopAtAria")}
            style={{
              margin: "8px 11px 4px",
              height: 40,
              borderRadius: 9,
              padding: "0 12px",
              background: "var(--hub-surface)",
              border: "1px solid var(--hub-border)",
              color: "var(--hub-text)",
              fontFamily: "system-ui",
              fontSize: 14,
            }}
          />
        )}
        {scheduledHint && (
          <p
            style={{
              margin: "8px 11px 4px",
              fontFamily: "ui-monospace, Menlo, monospace",
              fontSize: 12,
              color: "var(--hub-muted)",
            }}
          >
            {scheduledHint}
          </p>
        )}
      </div>
    </HubPopover>
  );
}
