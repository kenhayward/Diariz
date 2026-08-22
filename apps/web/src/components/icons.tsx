/// Shared inline icons for the left nav's drill-in list, search bar and room switcher. The app has no
/// icon package — glyphs are hand-inlined SVG on the Feather-style 24-grid, sized and stroked by
/// `iconProps` (see `ToolbarButton`). These are the ones the nav redesign needs that did not exist:
/// before this, chevrons/home were literal text characters (`▸ ▾ ⌂`), which cannot be stroked, sized or
/// coloured with the rest of the UI.
///
/// Each takes an optional `size` (default 18) because the nav mixes 14px row glyphs with 18px controls.
/// Decorative by default — a caller that needs a name passes `title`, which promotes the glyph to
/// `role="img"`; otherwise it is `aria-hidden` so screen readers read the row's text, not the icon.

import { iconProps } from "./ToolbarButton";

interface IconProps {
  size?: number;
  title?: string;
}

function svgProps({ size = 18, title }: IconProps) {
  return {
    ...iconProps,
    width: size,
    height: size,
    ...(title ? { role: "img", "aria-label": title } : { "aria-hidden": true }),
  };
}

export const SearchIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <circle cx="11" cy="11" r="8" />
    <line x1="21" y1="21" x2="16.65" y2="16.65" />
  </svg>
);

export const ChevronDownIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <polyline points="6 9 12 15 18 9" />
  </svg>
);

export const ChevronRightIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <polyline points="9 18 15 12 9 6" />
  </svg>
);

export const ArrowLeftIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <line x1="19" y1="12" x2="5" y2="12" />
    <polyline points="12 19 5 12 12 5" />
  </svg>
);

export const GlobeIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <circle cx="12" cy="12" r="10" />
    <line x1="2" y1="12" x2="22" y2="12" />
    <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
  </svg>
);

/// A calendar page. Marks a real calendar account (Google, desktop Outlook) on the Preferences Calendars
/// panel - a URL subscription takes `GlobeIcon` - a recording linked to a meeting in the list, and the
/// Calendars row in the Preferences nav. Providers are told apart by their tint and name, not by a
/// different shape: no logos are used.
///
/// `color` overrides `currentColor` with a linked calendar's own colour, as `FolderIcon` does for a
/// section. A caller that passes neither inherits its row, so the glyph inverts with an active nav tab.
export const CalendarIcon = ({ size = 18, title, color, className }: IconProps & { color?: string | null; className?: string }) => (
  <svg {...svgProps({ size, title })} className={className} style={color ? { color } : undefined}>
    <rect x="3" y="4" width="18" height="18" rx="2" ry="2" />
    <line x1="16" y1="2" x2="16" y2="6" />
    <line x1="8" y1="2" x2="8" y2="6" />
    <line x1="3" y1="10" x2="21" y2="10" />
  </svg>
);

/// Angle brackets - Feather `code`. Marks the REST API on the Integrations page. New here: the shape did
/// not exist anywhere in the app before.
export const CodeIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <polyline points="16 18 22 12 16 6" />
    <polyline points="8 6 2 12 8 18" />
  </svg>
);

/// Two arrows chasing each other - Feather `refresh-cw`. Marks outbound automations. Promoted from the
/// local copy in `nav/ListToolbar.tsx`, which predates this file and takes no size or title.
export const RefreshIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M23 4v6h-6" />
    <path d="M1 20v-6h6" />
    <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
  </svg>
);

/// A person - Feather `user`. The Preferences nav's Profile row.
export const UserIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
    <circle cx="12" cy="7" r="4" />
  </svg>
);

/// A page with lines - Feather `file-text`. The Preferences nav's Formulas row.
export const FileTextIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
    <polyline points="14 2 14 8 20 8" />
    <line x1="16" y1="13" x2="8" y2="13" />
    <line x1="16" y1="17" x2="8" y2="17" />
  </svg>
);

/// A speech bubble - Feather `message-square`. The Preferences nav's Assistant row.
export const MessageSquareIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
  </svg>
);

/// A microphone. Marks a recording in the meetings list, and the Recordings row in the Preferences nav.
/// `color` overrides `currentColor` for the list's audio-present/deleted states; the nav takes neither and
/// inherits the row, so the glyph inverts with it when the tab is active.
export const MicIcon = ({ size = 18, title, className }: IconProps & { className?: string }) => (
  <svg {...svgProps({ size, title })} className={className}>
    <path d="M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z" />
    <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
    <line x1="12" y1="19" x2="12" y2="23" />
    <line x1="8" y1="23" x2="16" y2="23" />
  </svg>
);

export const HomeIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
    <polyline points="9 22 9 12 15 12 15 22" />
  </svg>
);

/// The drill-in list's folder row glyph. Takes an explicit `color` (not `currentColor`): folder colour is
/// derived per section by `sectionColors`, and the row's text colour follows it.
export const FolderIcon = ({ size = 14, title, color }: IconProps & { color?: string }) => (
  <svg {...svgProps({ size, title })} stroke={color ?? "currentColor"}>
    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
  </svg>
);

/// The contextual-help glyph: a circled question mark placed next to a feature or field. Feather
/// `help-circle`, so it strokes and sizes with every other icon in the app (a solid white disc would
/// vanish against the light-mode white surfaces these buttons sit on).
export const HelpCircleIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <circle cx="12" cy="12" r="10" />
    <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3" />
    <line x1="12" y1="17" x2="12.01" y2="17" />
  </svg>
);

/// A luggage-tag outline with its punched hole. The recording hub's Tags pill glyph - the tag family's
/// only new shape here.
export const TagIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M20.6 13.4 12.6 21.4a2 2 0 0 1-2.8 0l-7-7a2 2 0 0 1-.6-1.4V4a2 2 0 0 1 2-2h8a2 2 0 0 1 1.4.6l7 7a2 2 0 0 1 0 2.8z" />
    <circle cx="7.5" cy="7.5" r="1.3" fill="currentColor" />
  </svg>
);

/// Two strands joining into one. Marks the auto-merge preference, matching the transcript toolbar's Merge
/// glyph so the setting and the manual action read as the same thing. The same shape is exported from
/// `detail/icons.tsx`, but as a fixed-size ReactElement, which does not compose with this file's `size`.
export const MergeIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M6 3v6a6 6 0 0 0 6 6 6 6 0 0 0 6-6V3" />
    <line x1="12" y1="15" x2="12" y2="21" />
  </svg>
);

/// Corner brackets around text lines - the standard "scan / read the text off this" glyph. Marks the two
/// extract-text actions in the screenshot viewer, where the destination (chat or a file) is carried by the
/// second glyph beside it rather than by this one.
export const ScanTextIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M3 7V5a2 2 0 0 1 2-2h2" />
    <path d="M17 3h2a2 2 0 0 1 2 2v2" />
    <path d="M21 17v2a2 2 0 0 1-2 2h-2" />
    <path d="M7 21H5a2 2 0 0 1-2-2v-2" />
    <line x1="7" y1="9" x2="17" y2="9" />
    <line x1="7" y1="13" x2="15" y2="13" />
    <line x1="7" y1="17" x2="12" y2="17" />
  </svg>
);

/// A tick. Confirms an action whose result lands somewhere the user cannot see from here - the chat
/// composer behind a modal, or a tab that is not open.
export const CheckIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <polyline points="20 6 9 17 4 12" />
  </svg>
);

/// An indeterminate progress ring, spun by Tailwind's `animate-spin`.
///
/// The gap in the ring is what makes the rotation visible - a closed circle spinning looks static. Takes a
/// `title` like the rest, but a caller should usually leave it decorative and put the status in the
/// button's own label, since a spinner announced on its own tells a screen reader nothing useful.
export const SpinnerIcon = ({ size = 18, title }: IconProps) => (
  <svg {...svgProps({ size, title })} className="animate-spin">
    <path d="M21 12a9 9 0 1 1-6.219-8.56" />
  </svg>
);
