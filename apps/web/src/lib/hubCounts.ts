/// The counts behind the hub tiles' subtitles ("142 segments · 21 min", "5 open · 2 done", "3 attached").
/// They come from four places — the recording DTO plus the three side queries the detail page already
/// runs (notes, attachments, formula results) — so this pulls them into one shape the tiles can read.

type RecordingLike = {
  durationMs: number;
  speakers: unknown[];
  current: { segments: { speaker: string }[] } | null;
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
    // The speakers heard in THIS transcript, not every Speaker row the recording has ever had.
    // Those rows are keyed (recording, label) and never deleted - that is what makes a rename survive a
    // re-transcribe - so a recording that was live-captured and then transcribed again holds the union
    // of every label it has ever carried. Counting rows counted history: one four-speaker meeting
    // reported twelve, while the Speakers page it links to correctly showed four.
    speakers: new Set((rec.current?.segments ?? []).map((s) => s.speaker)).size,
    notes: notes.length,
    files: attachments.length,
    formulaRuns: formulaResults.length,
    screenshots: shots.length,
  };
}
