import MeetingTypeIcon, { MEETING_TYPE_ICONS } from "./MeetingTypeIcon";

/// A shared icon + background-colour picker: a native colour input beside the fixed icon grid (the selected
/// icon carries a ring). Used by meeting types, and by rooms and groups. `onChange` reports a partial patch so a
/// caller can spread it into its draft. `colorLabel` is the accessible name for the colour input.
export default function IconColorPicker({
  icon,
  color,
  onChange,
  colorLabel,
}: {
  icon: string | null;
  color: string;
  onChange: (patch: { icon?: string; color?: string }) => void;
  colorLabel: string;
}) {
  return (
    <div className="flex items-center gap-2">
      <input
        type="color"
        value={color}
        onChange={(e) => onChange({ color: e.target.value })}
        aria-label={colorLabel}
        className="h-8 w-8 shrink-0 cursor-pointer rounded border p-0.5 dark:border-gray-700 dark:bg-gray-800"
      />
      <div className="flex flex-wrap gap-1">
        {MEETING_TYPE_ICONS.map((ic) => (
          <button
            key={ic}
            type="button"
            aria-label={ic}
            aria-pressed={icon === ic}
            onClick={() => onChange({ icon: ic })}
            className={`rounded p-0.5 ${icon === ic ? "ring-2 ring-indigo-500" : ""}`}
          >
            <MeetingTypeIcon icon={ic} color={color} size={22} />
          </button>
        ))}
      </div>
    </div>
  );
}
