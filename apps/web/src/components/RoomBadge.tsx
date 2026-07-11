import MeetingTypeIcon from "./MeetingTypeIcon";

const FALLBACK_COLOR = "#6b7280";

/// A shared room's badge: its chosen icon on the room colour (via the shared icon set), or - when no icon was
/// picked - a colour swatch bearing the room's first letter. Decorative (aria-hidden): callers render the
/// room name alongside it. Used in the room switcher and the Manage Rooms list.
export default function RoomBadge({
  icon,
  color,
  name,
  size = "sm",
}: {
  icon: string | null;
  color: string | null;
  name: string;
  size?: "xs" | "sm";
}) {
  if (icon) return <MeetingTypeIcon icon={icon} color={color || FALLBACK_COLOR} size={size === "xs" ? 24 : 32} />;

  const box = size === "xs" ? "h-6 w-6 text-[10px]" : "h-8 w-8 text-xs";
  return (
    <span
      className={`flex ${box} shrink-0 items-center justify-center rounded-md font-medium text-white`}
      style={{ backgroundColor: color ?? FALLBACK_COLOR }}
      aria-hidden="true"
    >
      {name.trim().charAt(0).toUpperCase() || "?"}
    </span>
  );
}
