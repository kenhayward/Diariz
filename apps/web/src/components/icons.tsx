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
