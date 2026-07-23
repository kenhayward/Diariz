/// The counts behind the hub tiles' subtitles ("142 segments · 21 min", "5 open · 2 done", "3 attached").
/// They come from four places — the recording DTO plus the three side queries the detail page already
/// runs (notes, attachments, formula results) — so this pulls them into one shape the tiles can read.

type RecordingLike = {
  durationMs: number;
  speakers: unknown[];
  current: { segments: unknown[] } | null;
  actions: { completed: boolean }[];
};

export interface HubCounts {
  segments: number;
  durationMs: number;
  actionsOpen: number;
  actionsDone: number;
  speakers: number;
  notes: number;
  files: number;
  formulaRuns: number;
  screenshots: number;
}

export function hubCounts(
  rec: RecordingLike,
  notes: unknown[],
  attachments: unknown[],
  formulaResults: unknown[],
  shots: unknown[] = [],
): HubCounts {
  return {
    segments: rec.current?.segments.length ?? 0,
    durationMs: rec.durationMs,
    actionsOpen: rec.actions.filter((a) => !a.completed).length,
    actionsDone: rec.actions.filter((a) => a.completed).length,
    speakers: rec.speakers.length,
    notes: notes.length,
    files: attachments.length,
    formulaRuns: formulaResults.length,
    screenshots: shots.length,
  };
}
