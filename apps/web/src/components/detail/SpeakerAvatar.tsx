import { initialsFromName } from "../../lib/initials";
import { speakerColorMap } from "../../lib/speakerColors";

/// A diarized speaker's initials on their assigned colour. Distinct from `Avatar`, which is the
/// signed-in user's monochrome bubble (and can carry a Google picture) — a speaker has no picture and
/// its whole job here is to be colour-identifiable at a glance, matching the transcript's flow track.
///
/// The text is near-black on every palette colour (they are all mid-brightness), which is what the
/// design specifies and what keeps the initials legible in both themes.

const SIZES = {
  xs: { box: "h-[18px] w-[18px] text-[8px]", ring: "ring-[1.5px]" },
  sm: { box: "h-7 w-7 text-[10px]", ring: "ring-2" },
  md: { box: "h-[34px] w-[34px] text-xs", ring: "ring-0" },
} as const;

export type SpeakerAvatarSize = keyof typeof SIZES;

export default function SpeakerAvatar({
  name,
  color,
  size = "sm",
  ringed,
}: {
  name: string;
  color: string;
  size?: SpeakerAvatarSize;
  /// Draws a background-coloured ring, so overlapping avatars in a stack read as separate discs.
  ringed?: boolean;
}) {
  const s = SIZES[size];
  return (
    <span
      title={name}
      style={{ backgroundColor: color }}
      className={`${s.box} flex shrink-0 items-center justify-center rounded-full font-bold text-gray-950 ${
        ringed ? `${s.ring} ring-white dark:ring-gray-800` : ""
      }`}
    >
      {initialsFromName(name)}
    </span>
  );
}

/// The overlapping run of speaker discs used on the hero chip and the Speakers tile. `labels` are the
/// diarization labels (which fix the colours); `nameOf` resolves each to its display name.
export function SpeakerAvatarStack({
  labels,
  nameOf,
  size = "sm",
  max = 5,
}: {
  labels: string[];
  nameOf: (label: string) => string;
  size?: SpeakerAvatarSize;
  max?: number;
}) {
  const colors = speakerColorMap(labels);
  const shown = labels.slice(0, max);
  const overflow = labels.length - shown.length;

  return (
    <span className="flex items-center">
      {shown.map((label, i) => (
        <span key={label} className={i === 0 ? "" : "-ml-1.5"}>
          <SpeakerAvatar name={nameOf(label)} color={colors.get(label) ?? "#60a5fa"} size={size} ringed />
        </span>
      ))}
      {overflow > 0 && (
        <span className="ml-1 text-[11px] font-medium text-gray-500 dark:text-gray-400">+{overflow}</span>
      )}
    </span>
  );
}
