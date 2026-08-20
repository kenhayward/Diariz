import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import HubPopover from "./HubPopover";
import { IconCamera, IconClock, IconPencil, IconUpload } from "./hubIcons";

/**
 * One menu row: the same glyph its icon button carries in the bar, plus the label that button only has on
 * its aria-label. Inert-by-handler rather than `disabled` for the same reason HubIconButton is - Chromium
 * does not dispatch mouse events to disabled form controls, so a disabled row's `title` never renders and
 * the reason it is unavailable can never be read.
 */
function Row({
  icon,
  label,
  disabledReason,
  onSelect,
}: {
  icon: ReactNode;
  label: string;
  disabledReason?: string;
  onSelect: () => void;
}) {
  const inert = disabledReason !== undefined;
  return (
    <button
      type="button"
      onClick={() => {
        if (inert) return;
        onSelect();
      }}
      aria-disabled={inert ? true : undefined}
      title={disabledReason ?? label}
      style={{
        display: "flex",
        alignItems: "center",
        gap: 11,
        width: "100%",
        padding: 11,
        borderRadius: 9,
        border: "none",
        background: "transparent",
        color: "var(--hub-text-2)",
        fontFamily: "system-ui",
        fontWeight: 500,
        fontSize: 14.5,
        textAlign: "left",
        cursor: inert ? "not-allowed" : "pointer",
        opacity: inert ? 0.5 : 1,
      }}
      onMouseEnter={(e) => {
        if (!inert) e.currentTarget.style.background = "var(--hub-surface-hover)";
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.background = "transparent";
      }}
    >
      {icon}
      <span>{label}</span>
    </button>
  );
}

export type MoreControlsPopoverProps = {
  open: boolean;
  onClose: () => void;
  /** Opens the Auto-stop popover. Always offered - auto-stop applies in both states. */
  onAutoStop: () => void;
  /** Reason auto-stop is unavailable (no permission, or an upload in flight), or undefined when usable. */
  autoStopDisabledReason?: string;
  /**
   * Opens the file dialog. Absent while recording, where upload is disabled anyway - a row that could only
   * ever be greyed is dead weight in a menu that exists precisely because room is short.
   */
  onUpload?: () => void;
  /** Reason upload is unavailable (no permission, or an upload already in flight), or undefined. */
  uploadDisabledReason?: string;
  /** Takes a screenshot. Absent unless the desktop shell can capture and a recording is running. */
  onCapture?: () => void;
  /** Reason capture is unavailable (no capture area yet), or undefined when usable. */
  captureDisabledReason?: string;
  /** Opens the Notes popover. Absent unless recording. */
  onNotes?: () => void;
};

/**
 * The capture bar's overflow menu, shown in place of the secondary icon buttons when the bar is too narrow
 * to hold them (see CaptureBar / Recorder for the tier thresholds).
 *
 * Choosing Auto-stop or Notes does NOT nest a popover inside this one: HubPopover panels are absolutely
 * positioned inside their own `relative` wrapper in the bar, so they drop from the bar rather than from
 * their trigger. The row simply toggles that popover's id, and the hub's one-open-at-a-time state closes
 * this menu on the way - which is exactly the behaviour wanted.
 *
 * Which rows exist is expressed by which handlers arrive, not by a `recording` flag: the two states differ
 * only in which controls are available, and this component has no business knowing why.
 */
export default function MoreControlsPopover({
  open,
  onClose,
  onAutoStop,
  autoStopDisabledReason,
  onUpload,
  uploadDisabledReason,
  onCapture,
  captureDisabledReason,
  onNotes,
}: MoreControlsPopoverProps) {
  const { t } = useTranslation("workspace");

  return (
    <HubPopover
      open={open}
      onClose={onClose}
      anchorClassName="right-0"
      width={240}
      ariaLabel={t("moreControls")}
    >
      <div style={{ padding: 6 }}>
        <Row
          icon={<IconClock />}
          label={t("autoStopLabel")}
          disabledReason={autoStopDisabledReason}
          onSelect={onAutoStop}
        />
        {onUpload && (
          <Row
            icon={<IconUpload />}
            label={t("recUpload")}
            disabledReason={uploadDisabledReason}
            onSelect={onUpload}
          />
        )}
        {onCapture && (
          <Row
            icon={<IconCamera />}
            label={t("screenshotCaptureButton")}
            disabledReason={captureDisabledReason}
            onSelect={onCapture}
          />
        )}
        {onNotes && <Row icon={<IconPencil />} label={t("liveNotesToggle")} onSelect={onNotes} />}
      </div>
    </HubPopover>
  );
}
