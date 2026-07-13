import { useId } from "react";
import { iconProps } from "./ToolbarButton";

/// The Formulas glyph (mirrors `images/formula-icon.svg`): a conical flask with a bright-blue liquid fill,
/// plus a sparkle to suggest AI-generated output. Feather-style stroke icon (strokes inherit `currentColor`
/// so it tints with its surroundings); only the liquid uses the blue gradient. `useId()` keeps the gradient
/// id unique when several icons render on one page.
export default function FlaskIcon() {
  const gid = useId();
  return (
    <svg {...iconProps}>
      <defs>
        <linearGradient id={gid} x1="0" y1="13" x2="0" y2="21" gradientUnits="userSpaceOnUse">
          <stop offset="0" stopColor="#38bdf8" />
          <stop offset="1" stopColor="#2563eb" />
        </linearGradient>
      </defs>
      {/* Liquid: fills the lower cone from the fill line (y=14) down through the rounded base. Fill only,
          no stroke, so it reads as liquid behind the outline. */}
      <path
        d="M6.6 14 L4.6 17.9 a2 2 0 0 0 1.7 3 h11.4 a2 2 0 0 0 1.7 -3 L17.4 14 Z"
        fill={`url(#${gid})`}
        stroke="none"
      />
      <path d="M9 2h6" />
      <path d="M10 2v6.5L4.5 18a2 2 0 0 0 1.7 3h11.6a2 2 0 0 0 1.7-3L14 8.5V2" />
      <path d="M6.5 14h11" />
      <path d="M19 3l.6 1.4L21 5l-1.4.6L19 7l-.6-1.4L17 5l1.4-.6z" />
    </svg>
  );
}
