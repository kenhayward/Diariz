import type { ReactNode } from "react";

/// The command hub's icon set, in one place.
///
/// Every glyph is a 24-viewBox path drawn in `currentColor` at `strokeWidth` 2 with round caps - the
/// pattern `CaptureControls.tsx` established - so a button's own colour, including its disabled opacity,
/// applies without the icon knowing anything about it. `IconPopOut` and `IconClose` moved here from
/// `NotesPopover.tsx` when the detached window started needing the same pair.

const Glyph = ({ size = 16, children }: { size?: number; children: ReactNode }) => (
  <svg
    viewBox="0 0 24 24"
    width={size}
    height={size}
    fill="none"
    aria-hidden="true"
    focusable="false"
    stroke="currentColor"
    strokeWidth={2}
    strokeLinecap="round"
    strokeLinejoin="round"
  >
    {children}
  </svg>
);

/// An arrow leaving a box: detach into a separate window.
export const IconPopOut = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M14 4h6v6M20 4l-8 8M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" />
  </Glyph>
);

export const IconClose = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M6 6l12 12M18 6L6 18" />
  </Glyph>
);

/// Write a note about this moment. A plus rather than a pencil: the pencil already means "edit this
/// line", and the two sit within a few pixels of each other in the stream.
export const IconPlus = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M12 5v14M5 12h14" />
  </Glyph>
);

export const IconPencil = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M4 20h4l10-10-4-4L4 16v4Z" />
  </Glyph>
);

export const IconCheck = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M5 13l4 4L19 7" />
  </Glyph>
);

/// A speech bubble with an arrow leaving it: send this into the chat.
export const IconChatArrow = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M21 11.5a8.4 8.4 0 0 1-8.5 8.4 8.9 8.9 0 0 1-3.9-.9L3 21l1.9-5.4a8.4 8.4 0 0 1-.9-3.8 8.4 8.4 0 0 1 8.5-8.4 8.4 8.4 0 0 1 8.5 8.4Z" />
    <path d="M8.5 12h7M12.5 9l3 3-3 3" />
  </Glyph>
);

/// A right arrow, for the capture overlay's Chat button where the bubble would be illegible at 11px.
export const IconArrowRight = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M5 12h13M13 6l6 6-6 6" />
  </Glyph>
);

/// Six dots: the universal "this can be dragged" mark.
export const IconGrip = ({ size }: { size?: number } = {}) => (
  <Glyph size={size}>
    <path d="M9 5h.01M9 12h.01M9 19h.01M15 5h.01M15 12h.01M15 19h.01" />
  </Glyph>
);
