/// Auditioning a person's own speech, one segment at a time.
///
/// Distinct from `segmentPlayback.ts`, which seeks a single `<audio>` element around one whole recording the
/// user owns. Assessment playback instead fetches a **clip per segment** from the API, because the audio may
/// live in a recording the listener has no other access to.
///
/// Deliberately no merging of adjacent segments: the clip endpoint only serves a span that falls inside a
/// single segment, and that guard is the entire reason the assessment permission cannot reach arbitrary
/// audio. Fetching one clip per segment as it is reached also means stopping after two seconds costs two
/// seconds of work rather than the whole selection.

export interface ClipRequest {
  segmentId: string;
  fromMs: number;
  toMs: number;
}

type SegmentLike = { id: string; startMs: number; endMs: number };

/// The selected segments as clip requests, in playback (time) order.
export function clipQueue(segments: SegmentLike[], selected: Iterable<string>): ClipRequest[] {
  const want = selected instanceof Set ? selected : new Set(selected);
  return segments
    .filter((s) => want.has(s.id))
    .slice()
    .sort((a, b) => a.startMs - b.startMs)
    .map((s) => ({ segmentId: s.id, fromMs: s.startMs, toMs: s.endMs }));
}
