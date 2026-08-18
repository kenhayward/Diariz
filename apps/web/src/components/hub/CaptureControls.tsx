import { useTranslation } from "react-i18next";
import HubIconButton from "./HubIconButton";

export type CaptureControlsProps = {
  /**
   * Whether the current recording has a capture area yet. Capturing without one opens the shell's area
   * picker and leaves every control inert until it is dismissed, which reads as the panel having frozen -
   * so capture waits until the area exists, making "set the area" the visible first step.
   */
  captureAreaSet: boolean;
  /**
   * Why the whole row is unavailable, or undefined when it is usable. A string rather than a boolean
   * because an inert icon button with no explanation is just a greyed glyph - the host knows the reason
   * (the pop-out window's channel to its host has gone) and this component does not, so it takes the copy
   * rather than inventing it.
   */
  unavailableReason?: string;
  onCapture: () => void;
  onChangeArea: () => void;
};

// Glyph wrapper matching the command hub's icons (see Recorder's HubIcon) at the popover's 16px size.
// Drawn in `currentColor` so each button's own text colour - including its disabled opacity - applies.
const Glyph = ({ children }: { children: React.ReactNode }) => (
  <svg viewBox="0 0 24 24" width="16" height="16" fill="none" aria-hidden="true" focusable="false"
    stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    {children}
  </svg>
);

/// Camera: body, shutter bump, lens. The one universally-read glyph in this set.
const IconCapture = () => (
  <Glyph>
    <path d="M4 8h3l1.5-2h7L17 8h3a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1Z" />
    <circle cx="12" cy="13" r="3.2" />
  </Glyph>
);

/// Crop marks. Deliberately no circle and no filled mass, so its silhouette cannot be mistaken for the
/// camera beside it at 16px.
const IconCaptureArea = () => (
  <Glyph>
    <path d="M7 3v14h14" />
    <path d="M3 7h14v14" />
  </Glyph>
);

/**
 * The screenshot capture controls: capture now, and change the capture area. Icon-only, because three of
 * these share one row inside a 400px popover and the text labels no longer fit; each button's short name
 * lives on aria-label and its fuller description on the hover tooltip.
 *
 * Rendered by both the notes popover and the detached notes window, which previously carried their own
 * near-identical copies of this row (and had already drifted apart on when capture is disabled). Only the
 * desktop shell can capture at all, so both hosts render this only when the shell bridge is present.
 */
export default function CaptureControls({
  captureAreaSet,
  unavailableReason,
  onCapture,
  onChangeArea,
}: CaptureControlsProps) {
  const { t } = useTranslation("workspace");

  // Both buttons go inert when the host is unreachable; only capture additionally waits on the area.
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
      <HubIconButton
        size="sm"
        label={t("screenshotCaptureButton")}
        title={t("screenshotCaptureButtonHint")}
        disabledReason={unavailableReason ?? (captureAreaSet ? undefined : t("screenshotCaptureNeedsArea"))}
        onClick={onCapture}
      >
        <IconCapture />
      </HubIconButton>
      <HubIconButton
        size="sm"
        label={t("screenshotCaptureArea")}
        title={t("screenshotCaptureAreaHint")}
        disabledReason={unavailableReason}
        onClick={onChangeArea}
      >
        <IconCaptureArea />
      </HubIconButton>
    </div>
  );
}
