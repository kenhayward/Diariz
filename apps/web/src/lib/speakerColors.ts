/// Colour assignment for diarized speakers. Speakers have no stored colour (unlike rooms and meeting
/// types, which carry a user-chosen one), so the hub derives one: sort the recording's labels and deal
/// out the palette in order. That keeps a speaker's colour stable across renders and across the views
/// that show them together — the hero avatar stack, the speaker tiles, and the transcript's
/// conversation-flow track all agree without threading a colour through every prop.
///
/// The palette is the design's speaker set (blue / pink / green / amber), extended so recordings with
/// many speakers still get distinct hues before it wraps. These are literal hex, not Tailwind classes:
/// they are data swatches and are identical in light and dark, matching `RoomBadge` / `MeetingTypeIcon`.

export const SPEAKER_PALETTE: readonly string[] = [
  "#60a5fa", // blue
  "#f472b6", // pink
  "#34d399", // green
  "#fbbf24", // amber
  "#a78bfa", // purple
  "#38bdf8", // cyan
  "#fb923c", // orange
  "#4ade80", // lime
];

/// Every distinct label mapped to its palette colour, dealt out in sorted label order (so the mapping
/// does not depend on the order segments happen to arrive in). Wraps once the labels outnumber the palette.
export function speakerColorMap(labels: string[]): Map<string, string> {
  const distinct = [...new Set(labels)].sort();
  return new Map(distinct.map((label, i) => [label, SPEAKER_PALETTE[i % SPEAKER_PALETTE.length]]));
}

/// The colour for one label, given every label in the recording. Unknown labels fall back to the first
/// palette entry rather than throwing — a segment can name a speaker the summary list doesn't.
export function speakerColorFor(label: string, labels: string[]): string {
  return speakerColorMap(labels).get(label) ?? SPEAKER_PALETTE[0];
}
