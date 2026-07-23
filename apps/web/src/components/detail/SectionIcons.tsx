import FlaskIcon from "../FlaskIcon";

/// The recording-hub icon set. Two families:
///
/// - **Card glyphs** (`TranscriptGlyph` … `FormulasGlyph`) — the duotone icons in the tile headers. The
///   outline inherits `currentColor` (so the tile tints it with its section colour) and each carries one
///   small filled accent in a literal hex, exactly as the design specifies. The accent colours are
///   deliberate design details, not theme colours, so they do not vary between light and dark.
/// - **Utility icons** — the stroke icons used by the hero chips, the tiles' action buttons, the
///   breadcrumb, and the header cluster.
///
/// Feather-style: 24-grid, `fill:none`, round caps/joins, stroke inherits `currentColor`. Every icon
/// takes a `size` so the same glyph can serve a 32px tile header and a 13px chip.

const base = (size: number) => ({
  width: size,
  height: size,
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: 2,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  "aria-hidden": true,
});

type IconProps = { size?: number };

// ---- Card glyphs (duotone) ----

/// Speech bubble + an amber spark (the transcript is machine-produced).
export function TranscriptGlyph({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M3 5.5h12.5A1.5 1.5 0 0 1 17 7v6a1.5 1.5 0 0 1-1.5 1.5H8l-4 3.2V14.5H3A1.5 1.5 0 0 1 1.5 13V7A1.5 1.5 0 0 1 3 5.5z" />
      <path d="M4.5 8.8h9" />
      <path d="M4.5 11.2h6" />
      <path d="M20 3.2l.62 1.66 1.66.62-1.66.62L20 7.78l-.62-1.66-1.66-.62 1.66-.62z" fill="#fbbf24" stroke="none" />
    </svg>
  );
}

/// Clipboard + a green tick (the checked-off action).
export function ActionsGlyph({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <rect x="5" y="4" width="14" height="17" rx="2" />
      <path d="M9.5 4V3.2A.8.8 0 0 1 10.3 2.4h3.4a.8.8 0 0 1 .8.8V4z" fill="currentColor" stroke="none" />
      <path d="M8.4 12.6l2.4 2.4 4.8-5.2" stroke="#34d399" />
    </svg>
  );
}

/// Person + cyan sound waves (a diarized voice).
export function SpeakersGlyph({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <circle cx="9" cy="8" r="3.2" />
      <path d="M3.5 20a5.5 5.5 0 0 1 11 0" />
      <path d="M16.4 6.6a6 6 0 0 1 0 10.8" stroke="#38bdf8" />
      <path d="M19 4a9 9 0 0 1 0 16" stroke="#38bdf8" opacity=".55" />
    </svg>
  );
}

/// Note page + an amber folded corner.
export function NotesGlyph({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M6 3.5h7.3L18 8.2V19.5A1 1 0 0 1 17 20.5H6A1 1 0 0 1 5 19.5v-15A1 1 0 0 1 6 3.5z" />
      <path d="M13.2 3.6l4.4 4.4h-4.4z" fill="#fbbf24" stroke="none" />
      <path d="M8 12.5h6.4" />
      <path d="M8 15.4h4.4" />
    </svg>
  );
}

/// Stacked documents + a purple folded corner.
export function FilesGlyph({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M8.5 5.5h5L17 9v8.5a1 1 0 0 1-1 1H8.5a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1z" />
      <path d="M13.2 5.6L17 9.4h-3.8z" fill="#a78bfa" stroke="none" />
      <path d="M5.5 8v10.5a1.5 1.5 0 0 0 1.5 1.5h7" opacity=".5" />
    </svg>
  );
}

/// The Formulas glyph already exists as the flask that mirrors `images/formula-icon.svg` — reuse it
/// rather than drawing a second one, so the hub and the rest of the app stay in step.
export { FlaskIcon as FormulasGlyph };

/// The minutes/document glyph on the hero card's green icon tile.
export function MinutesGlyph({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
      <path d="M14 2v6h6" />
      <path d="M8 13h8" />
      <path d="M8 17h6" />
    </svg>
  );
}

// ---- Utility icons ----

export function CalendarIcon({ size = 13 }: IconProps) {
  return (
    <svg {...base(size)}>
      <rect x="3" y="4.5" width="18" height="16.5" rx="2" />
      <line x1="3" y1="9.5" x2="21" y2="9.5" />
      <line x1="8" y1="2.5" x2="8" y2="6.5" />
      <line x1="16" y1="2.5" x2="16" y2="6.5" />
    </svg>
  );
}

export function ClockIcon({ size = 13 }: IconProps) {
  return (
    <svg {...base(size)}>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3.5 2" />
    </svg>
  );
}

export function AudioIcon({ size = 13 }: IconProps) {
  return (
    <svg {...base(size)}>
      <line x1="4" y1="10" x2="4" y2="14" />
      <line x1="8" y1="6" x2="8" y2="18" />
      <line x1="12" y1="9" x2="12" y2="15" />
      <line x1="16" y1="4" x2="16" y2="20" />
      <line x1="20" y1="8" x2="20" y2="16" />
    </svg>
  );
}

export function GlobeIcon({ size = 13 }: IconProps) {
  return (
    <svg {...base(size)}>
      <circle cx="12" cy="12" r="9" />
      <path d="M3 12h18" />
      <path d="M12 3a15 15 0 0 1 0 18 15 15 0 0 1 0-18" />
    </svg>
  );
}

export function TemplateIcon({ size = 13 }: IconProps) {
  return (
    <svg {...base(size)}>
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <line x1="3" y1="9" x2="21" y2="9" />
      <line x1="9" y1="21" x2="9" y2="9" />
    </svg>
  );
}

export function PlayIcon({ size = 15 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M7 4l13 8-13 8z" fill="currentColor" stroke="none" />
    </svg>
  );
}

export function PauseIcon({ size = 15 }: IconProps) {
  return (
    <svg {...base(size)}>
      <rect x="6" y="4" width="4" height="16" rx="1" fill="currentColor" stroke="none" />
      <rect x="14" y="4" width="4" height="16" rx="1" fill="currentColor" stroke="none" />
    </svg>
  );
}

export function ChevronRightIcon({ size = 15 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M9 6l6 6-6 6" />
    </svg>
  );
}

export function BackIcon({ size = 15 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M19 12H5" />
      <path d="M12 19l-7-7 7-7" />
    </svg>
  );
}

export function PlusIcon({ size = 14 }: IconProps) {
  return (
    <svg {...base(size)}>
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  );
}

export function LinkIcon({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
      <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
    </svg>
  );
}

export function PencilIcon({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M17 3a2.83 2.83 0 0 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
    </svg>
  );
}

export function DownloadIcon({ size = 18 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
      <path d="M7 10l5 5 5-5" />
      <line x1="12" y1="15" x2="12" y2="3" />
    </svg>
  );
}

export function FileIcon({ size = 13 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
      <path d="M14 2v6h6" />
    </svg>
  );
}

export function UserIcon({ size = 13 }: IconProps) {
  return (
    <svg {...base(size)}>
      <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
      <circle cx="12" cy="7" r="4" />
    </svg>
  );
}
