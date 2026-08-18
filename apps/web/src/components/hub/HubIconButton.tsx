import type { ReactNode } from "react";

export type HubIconButtonProps = {
  /** Accessible name (aria-label) and default tooltip. */
  label: string;
  /** Tooltip override; falls back to `label`. */
  title?: string;
  onClick: () => void;
  /** Unavailable with nothing to explain: a genuinely disabled control, skipped in the tab order. */
  disabled?: boolean;
  /**
   * Unavailable *for a stated reason*: the button goes inert but stays hoverable and focusable, and this
   * text becomes its tooltip. Use it whenever the user could act on the explanation ("Set a capture area
   * first"), which is most of the time - see the note on the button below for why `disabled` cannot carry
   * one. Takes precedence over `disabled` and over `title`.
   */
  disabledReason?: string;
  /** Whether the button controls an open popover (sets aria-expanded + aria-haspopup). */
  expanded?: boolean;
  /**
   * For a sticky control - one that stays on until pressed again. Sets aria-pressed and tints the
   * button, so "this is running right now" is legible both to a screen reader and at a glance. Omit
   * entirely for an ordinary button; `false` makes it a toggle that is currently off.
   */
  pressed?: boolean;
  /** 44px in the command hub (default); 28px inside a popover, where several share one row. */
  size?: "lg" | "sm";
  children: ReactNode;
};

const SIZES = {
  lg: { box: 44, radius: 11 },
  sm: { box: 28, radius: 8 },
} as const;

/**
 * The shared command-hub icon button used for Auto-stop, Upload, Notes and the capture controls. Transparent
 * bg, a hairline `--hub-border`, muted `--hub-text-2` icon colour, and a `--hub-surface-hover` background on
 * hover. Icon-only: the meaning lives on the glyph child; the label lives on aria-label (+ title) so the
 * button's accessible name survives and hover tooltips work.
 *
 * Two flavours of unavailable, deliberately distinct - see `disabled` vs `disabledReason` above.
 */
export default function HubIconButton({
  label,
  title,
  onClick,
  disabled,
  disabledReason,
  expanded,
  pressed,
  size = "lg",
  children,
}: HubIconButtonProps) {
  // A reason means the button must stay hoverable to show it, so it is inert-by-handler rather than
  // `disabled`: Chromium does not dispatch mouse events to disabled form controls, so a disabled button's
  // `title` never renders. On an icon-only button that leaves a greyed glyph and no way to find out why.
  const inert = disabledReason !== undefined;
  const unavailable = inert || disabled === true;
  const { box, radius } = SIZES[size];

  return (
    <button
      type="button"
      onClick={() => {
        if (unavailable) return;
        onClick();
      }}
      disabled={!inert && disabled}
      aria-label={label}
      aria-disabled={inert ? true : undefined}
      aria-pressed={pressed}
      title={disabledReason ?? title ?? label}
      {...(expanded !== undefined ? { "aria-haspopup": "dialog" as const, "aria-expanded": expanded } : {})}
      style={{
        width: box,
        height: box,
        flexShrink: 0,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        borderRadius: radius,
        background: pressed ? "var(--hub-surface-hover)" : "transparent",
        border: `1px solid ${pressed ? "var(--hub-red)" : "var(--hub-border)"}`,
        color: pressed ? "var(--hub-text)" : "var(--hub-text-2)",
        cursor: unavailable ? "not-allowed" : "pointer",
        opacity: unavailable ? 0.5 : 1,
      }}
      onMouseEnter={(e) => {
        if (!unavailable) e.currentTarget.style.background = "var(--hub-surface-hover)";
      }}
      onMouseLeave={(e) => {
        e.currentTarget.style.background = pressed ? "var(--hub-surface-hover)" : "transparent";
      }}
    >
      {children}
    </button>
  );
}
