import type { ReactNode } from "react";

export type HubIconButtonProps = {
  /** Accessible name (aria-label) and default tooltip. */
  label: string;
  /** Tooltip override; falls back to `label`. */
  title?: string;
  onClick: () => void;
  disabled?: boolean;
  /** Whether the button controls an open popover (sets aria-expanded + aria-haspopup). */
  expanded?: boolean;
  children: ReactNode;
};

/**
 * The shared 44x44 command-hub icon button used for Auto-stop, Upload and Notes. Transparent bg, a hairline
 * `--hub-border`, muted `--hub-text-2` icon colour, and a `--hub-surface-hover` background on hover. Icon-only:
 * the meaning lives on the glyph child; the label lives on aria-label (+ title) so the button's accessible name
 * survives and hover tooltips work.
 */
export default function HubIconButton({
  label,
  title,
  onClick,
  disabled,
  expanded,
  children,
}: HubIconButtonProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      title={title ?? label}
      {...(expanded !== undefined ? { "aria-haspopup": "dialog" as const, "aria-expanded": expanded } : {})}
      style={{
        width: 44,
        height: 44,
        flexShrink: 0,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        borderRadius: 11,
        background: "transparent",
        border: "1px solid var(--hub-border)",
        color: "var(--hub-text-2)",
        cursor: disabled ? "not-allowed" : "pointer",
        opacity: disabled ? 0.5 : 1,
      }}
      onMouseEnter={(e) => {
        if (!disabled) e.currentTarget.style.background = "var(--hub-surface-hover)";
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.background = "transparent";
      }}
    >
      {children}
    </button>
  );
}
