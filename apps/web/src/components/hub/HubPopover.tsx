import { useEffect, type ReactNode } from "react";

type HubPopoverProps = {
  /** Whether the popover is shown. The parent owns this so only one popover is open at a time. */
  open: boolean;
  /** Called when the user dismisses the popover (backdrop click or Escape). */
  onClose: () => void;
  /** Extra classes for the absolutely-positioned panel (e.g. `right-0` to align under the trigger). */
  anchorClassName?: string;
  /** Panel width in px. Defaults to 320. */
  width?: number;
  /** Accessible label for the dialog. */
  ariaLabel?: string;
  children: ReactNode;
};

/**
 * Reusable popover shell for the top-bar command hub. The caller wraps its trigger + this popover in a
 * `relative` container; the panel renders `absolute` just below the 80px bar, anchored near the trigger.
 * A full-screen click-away backdrop and Escape both call `onClose`. The parent controls `open`, so the
 * single `openPopover` state the hub keeps enforces "one open at a time".
 */
export default function HubPopover({
  open,
  onClose,
  anchorClassName = "",
  width = 320,
  ariaLabel,
  children,
}: HubPopoverProps) {
  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <>
      {/* Full-screen click-away backdrop, below the panel. */}
      <div
        data-testid="hub-popover-backdrop"
        onClick={onClose}
        className="fixed inset-0 z-40"
        style={{ background: "rgba(4,8,15,.45)" }}
      />
      <div
        role="dialog"
        aria-label={ariaLabel}
        className={`absolute top-[calc(100%+8px)] z-50 ${anchorClassName}`}
        style={{
          width,
          background: "var(--hub-popover-bg)",
          border: "1px solid var(--hub-popover-border)",
          borderRadius: 14,
          boxShadow: "var(--hub-popover-shadow)",
          color: "var(--hub-text)",
          animation: "popIn .14s ease",
        }}
      >
        {children}
      </div>
    </>
  );
}
