import MeetingTypeIcon from "./MeetingTypeIcon";

const FALLBACK_COLOR = "#6b7280";

/// A shared room's badge: its chosen icon on the room colour (via the shared icon set), or - when no icon was
/// picked - a colour swatch bearing the room's first letter. Decorative (aria-hidden): callers render the
/// room name alongside it. Used in the room switcher and the Manage Rooms list.
const PX: Record<string, number> = { "2xs": 16, xs: 24, sm: 32 };
const BOX: Record<string, string> = { "2xs": "h-4 w-4 text-[9px]", xs: "h-6 w-6 text-[10px]", sm: "h-8 w-8 text-xs" };

export default function RoomBadge({
  icon,
  color,
  name,
  size = "sm",
}: {
  icon: string | null;
  color: string | null;
  name: string;
  size?: "2xs" | "xs" | "sm";
}) {
  if (icon) return <MeetingTypeIcon icon={icon} color={color || FALLBACK_COLOR} size={PX[size]} />;

  const box = BOX[size];
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
