import type { ReactElement } from "react";

/// The fixed set of meeting-type icons. Keys match the backend allow-list (`MeetingTypeIcons.All`), so a saved
/// `icon` always resolves here. Each is a 16x16 line glyph (stroke = `currentColor`) shown on the type's colour.

/// The icon keys, in the order the picker offers them. Keep in sync with the backend `MeetingTypeIcons.All`.
export const MEETING_TYPE_ICONS = [
  "document", "handshake", "refresh", "calendar", "user", "users", "clipboard", "megaphone",
  "video", "chat", "phone", "check", "star", "briefcase", "flag", "chart",
] as const;

export type MeetingTypeIconKey = (typeof MEETING_TYPE_ICONS)[number];

/// The 16x16 path(s) for each icon (drawn with stroke=currentColor, no fill).
const PATHS: Record<string, ReactElement> = {
  document: <path d="M4 2h6l3 3v9H4V2zM10 2v3h3" />,
  handshake: <path d="M2 8l3-3 3 2 3-2 3 3-3 4-2-2-2 2-3-4z" />,
  refresh: <path d="M13 8a5 5 0 1 1-1.5-3.5M13 2v3h-3" />,
  calendar: <path d="M3 4h10v10H3V4zM3 7h10M6 2v3M10 2v3" />,
  user: <path d="M8 8a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5zM3 14c0-2.5 2.2-4 5-4s5 1.5 5 4" />,
  users: <path d="M6 8a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM2 14c0-2 1.8-3 4-3s4 1 4 3M11 5a2 2 0 0 1 0 4M14 13c0-1.5-1-2.3-2.5-2.7" />,
  clipboard: <path d="M5 3h6v11H5V3zM6 3V2h4v1M7 7h3M7 10h3" />,
  megaphone: <path d="M3 7v2l7 3V4L3 7zM3 7H2v2h1M6 11v2" />,
  video: <path d="M2 4h8v8H2V4zM10 7l4-2v6l-4-2" />,
  chat: <path d="M2 3h12v8H6l-3 3v-3H2V3z" />,
  phone: <path d="M4 2l2 3-1.5 1.5a8 8 0 0 0 3 3L11 8l3 2-1.5 2.5a1 1 0 0 1-1 .5A11 11 0 0 1 3.5 4.5a1 1 0 0 1 .5-1L4 2z" />,
  check: <path d="M3 8l3.5 3.5L13 4" />,
  star: <path d="M8 2l1.8 3.8 4.2.5-3 2.9.8 4.1L8 11.4 4.2 13.3l.8-4.1-3-2.9 4.2-.5L8 2z" />,
  briefcase: <path d="M2 6h12v7H2V6zM6 6V4h4v2M2 9h12" />,
  flag: <path d="M4 2v12M4 3h8l-2 2.5L12 8H4" />,
  chart: <path d="M2 14V2M2 14h12M5 11v-3M8 11V5M11 11V8" />,
};

/// A meeting type's icon rendered on a rounded swatch of its colour. `size` is the swatch edge in px.
export default function MeetingTypeIcon({
  icon,
  color,
  size = 20,
  title,
}: {
  icon: string;
  color: string;
  size?: number;
  title?: string;
}) {
  const glyph = PATHS[icon] ?? PATHS.document;
  return (
    <span
      className="inline-flex shrink-0 items-center justify-center rounded text-white"
      style={{ backgroundColor: color || "#5C6BC0", width: size, height: size }}
      title={title}
      aria-hidden={title ? undefined : true}
    >
      <svg
        viewBox="0 0 16 16"
        width={Math.round(size * 0.7)}
        height={Math.round(size * 0.7)}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.4}
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        {glyph}
      </svg>
    </span>
  );
}
