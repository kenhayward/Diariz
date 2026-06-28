import type { ReactNode } from "react";

/// Feather-style icon props (stroke, 24-grid) shared by the app's inline SVG icons.
export const iconProps = {
  width: 18,
  height: 18,
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 2,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
};

/// A single graphical toolbar button: an icon with a hover tooltip + accessible name. Shared by the
/// recording-detail toolbar and the recordings-list toolbar. When `active`, it renders highlighted.
export default function ToolbarButton({
  label,
  onClick,
  icon,
  disabled,
  active,
}: {
  label: string;
  onClick: () => void;
  icon: ReactNode;
  disabled?: boolean;
  active?: boolean;
}) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      aria-pressed={active}
      disabled={disabled}
      onClick={onClick}
      className={`rounded px-1.5 py-1 disabled:pointer-events-none disabled:opacity-40 ${
        active
          ? "bg-blue-50 text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
          : "text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
      }`}
    >
      {icon}
    </button>
  );
}
