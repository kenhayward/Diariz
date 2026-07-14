/// Which section of the recording-detail page is showing. The page is a hub (the landing grid of tiles)
/// plus the sections you drill into from it — the old tab strip's keys, minus `overview`, whose content
/// the hub's hero card absorbed.
///
/// The choice is persisted across recordings (one key for the whole page, as the tab strip was), so
/// someone who lives in the transcript keeps landing there.

export type SectionKey =
  | "hub"
  | "transcript"
  | "minutes"
  | "actions"
  | "notes"
  | "speakers"
  | "files"
  | "formulas";

export const DETAIL_SECTION_KEY = "diariz.detailSection";

const SECTIONS: readonly SectionKey[] = [
  "hub",
  "transcript",
  "minutes",
  "actions",
  "notes",
  "speakers",
  "files",
  "formulas",
];

/// The tab strip persisted two keys that are no longer sections: `overview` became the hub, and
/// `attachments` was renamed `files`. Both are still sitting in real users' localStorage, so map them
/// rather than dropping them onto a section that no longer exists.
const LEGACY: Record<string, SectionKey> = {
  overview: "hub",
  attachments: "files",
};

/// The section to open with. A `?t=` segment deep-link always wins — the link points at a moment in the
/// transcript, so that is what the user asked to see, whatever they were last looking at.
export function initialSection(stored: string | null, deepLinked: boolean): SectionKey {
  if (deepLinked) return "transcript";
  if (!stored) return "hub";
  if (stored in LEGACY) return LEGACY[stored];
  return SECTIONS.includes(stored as SectionKey) ? (stored as SectionKey) : "hub";
}
