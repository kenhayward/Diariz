import { iconProps } from "./ToolbarButton";

/// The Formulas glyph (mirrors `images/formula-icon.svg`): a conical flask with liquid, plus a sparkle to
/// suggest AI-generated output. Feather-style stroke icon, same inline-component convention as the app's
/// other icons (ActionsToolbar's RefreshIcon/SelectIcon, RecordingDetail's toolbar icons).
export default function FlaskIcon() {
  return (
    <svg {...iconProps}>
      <path d="M9 2h6" />
      <path d="M10 2v6.5L4.5 18a2 2 0 0 0 1.7 3h11.6a2 2 0 0 0 1.7-3L14 8.5V2" />
      <path d="M6.5 14h11" />
      <path d="M19 3l.6 1.4L21 5l-1.4.6L19 7l-.6-1.4L17 5l1.4-.6z" />
    </svg>
  );
}
