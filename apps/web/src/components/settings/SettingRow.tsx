import { useId, type ReactNode } from "react";

/// The column tracks every settings section shares: name, control, description.
///
/// **Fixed widths, not content-derived, and deliberately one shared constant.** Sized to content, each
/// section would size its own first column from its own longest label, and the controls would step in and
/// out down the page - the settings live in two components, so there is no single grid to size them
/// together. Fixed tracks make the alignment independent of what any section happens to contain.
///
/// Below `md` it collapses to one column, which stacks name, control and description exactly as they were
/// before: three columns of this width cannot fit a narrow window, and a description squeezed into a few
/// characters is worse than one on its own line.
export const SETTING_GRID =
  "grid grid-cols-1 gap-x-5 gap-y-2 md:grid-cols-[13rem_15rem_minmax(0,1fr)] md:items-center md:gap-y-2.5";

/// One setting on one line.
///
/// Renders three cells straight into the parent grid - it returns a fragment, so there is no wrapper
/// element to break the tracks. `children` is a render prop taking the id to put on the control, because
/// the name is a sibling `<label htmlFor>` rather than a wrapper: a wrapping label cannot span grid cells.
export default function SettingRow({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: (id: string) => ReactNode;
}) {
  const id = useId();
  return (
    <>
      <label
        htmlFor={id}
        className="text-sm font-medium text-gray-700 dark:text-gray-200 md:justify-self-start"
      >
        {label}
      </label>
      <div className="min-w-0">{children(id)}</div>
      {/* Kept in the row rather than dropped: the descriptions are what make these settings answerable.
          `text-balance` stops a two-line description breaking after one word. */}
      <p className="text-xs text-balance text-gray-500 dark:text-gray-400">{hint}</p>
    </>
  );
}

/// The shared look for a control in the middle column. Full width of its track, so every control starts and
/// ends on the same two vertical lines whatever it is.
export const SETTING_CONTROL =
  "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";
