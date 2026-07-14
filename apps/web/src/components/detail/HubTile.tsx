import type { KeyboardEvent, ReactNode } from "react";
import { ChevronRightIcon } from "./SectionIcons";

/// One capability tile on the recording hub: a coloured glyph, a title, a count subtitle, and a preview
/// of the section's actual contents. The whole tile opens the section.
///
/// Some tiles also carry an in-place action (Notes "+ New", Files "+ Add", Formulas "Run"). A `<button>`
/// cannot be nested inside an `<a>`, so the tile is a `role="button"` div — keyboard-operable via Enter
/// and Space — and the action button lives inside it and stops propagation, so pressing it opens the
/// modal instead of navigating.
///
/// Tiles are content-height and top-aligned by the grid (`align-content: start`); they must not stretch
/// to fill the row, or short tiles gain a slab of dead space.

const COLORS = {
  blue: { tint: "bg-blue-500/15", fg: "text-blue-600 dark:text-blue-400" },
  amber: { tint: "bg-amber-500/15", fg: "text-amber-600 dark:text-amber-400" },
  pink: { tint: "bg-pink-500/15", fg: "text-pink-600 dark:text-pink-400" },
  purple: { tint: "bg-violet-500/15", fg: "text-violet-600 dark:text-violet-400" },
  cyan: { tint: "bg-sky-500/15", fg: "text-sky-600 dark:text-sky-400" },
} as const;

export type TileColor = keyof typeof COLORS;

export default function HubTile({
  color,
  icon,
  title,
  subtitle,
  action,
  onOpen,
  children,
}: {
  color: TileColor;
  icon: ReactNode;
  title: string;
  subtitle: string;
  /// The in-place action button (Notes / Files / Formulas). When absent the tile shows a nav chevron.
  action?: ReactNode;
  onOpen: () => void;
  /// The tile's preview body: a waveform, a checklist, avatars, a file list, a note excerpt.
  children?: ReactNode;
}) {
  const c = COLORS[color];

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== "Enter" && e.key !== " ") return;
    e.preventDefault();
    onOpen();
  };

  return (
    <div
      role="button"
      tabIndex={0}
      aria-label={title}
      onClick={onOpen}
      onKeyDown={onKeyDown}
      className="flex cursor-pointer flex-col rounded-2xl border border-gray-200 bg-white p-4 text-left transition-colors hover:border-gray-300 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:border-gray-700 dark:bg-gray-800 dark:hover:border-gray-600"
    >
      <div className="flex items-center gap-2.5">
        <span className={`grid h-8 w-8 shrink-0 place-items-center rounded-[9px] ${c.tint} ${c.fg}`}>{icon}</span>
        <div className="min-w-0">
          <div className="truncate text-sm font-semibold text-gray-900 dark:text-gray-100">{title}</div>
          <div className="truncate text-[11.5px] text-gray-500 dark:text-gray-400">{subtitle}</div>
        </div>
        {action ? (
          // The action opens a modal; without this the click would bubble and also navigate into the section.
          <div className="ml-auto shrink-0" onClick={(e) => e.stopPropagation()} onKeyDown={(e) => e.stopPropagation()}>
            {action}
          </div>
        ) : (
          <span className="ml-auto shrink-0 text-gray-400 dark:text-gray-500">
            <ChevronRightIcon />
          </span>
        )}
      </div>
      {children && <div className="mt-3">{children}</div>}
    </div>
  );
}

/// The pill button in a tile header. `accent` fills it (Formulas' "Run"); otherwise it is the quiet
/// bordered variant used by "+ New" and "+ Add".
export function TileAction({
  label,
  icon,
  onClick,
  accent,
}: {
  label: string;
  icon?: ReactNode;
  onClick: () => void;
  accent?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      className={`inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1 text-[11.5px] font-semibold ${
        accent
          ? "bg-blue-600 text-white hover:bg-blue-700"
          : "border border-gray-200 bg-gray-50 text-blue-600 hover:bg-gray-100 dark:border-gray-600 dark:bg-gray-700/60 dark:text-blue-300 dark:hover:bg-gray-700"
      }`}
    >
      {icon}
      {label}
    </button>
  );
}
