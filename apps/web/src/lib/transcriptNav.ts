/// Helpers for the chat's clickable transcript references: parsing the in-app deep-links the tools emit
/// (`/recordings/{id}?t={ms}`) and locating the segment at a millisecond position.

export interface RecordingLink {
  id: string;
  /// The segment time position in ms, or null when the link targets the whole recording.
  t: number | null;
}

/// Parses an `<a href>` that points at an in-app recording. Returns null for anything else (external
/// links, other routes), so the chat only intercepts its own transcript links.
export function parseRecordingLink(href: string): RecordingLink | null {
  try {
    const url = new URL(href, window.location.origin);
    if (url.origin !== window.location.origin) return null;
    const m = url.pathname.match(/^\/recordings\/([^/]+)\/?$/);
    if (!m) return null;
    const raw = url.searchParams.get("t");
    const t = raw != null && /^\d+$/.test(raw) ? Number(raw) : null;
    return { id: decodeURIComponent(m[1]), t };
  } catch {
    return null;
  }
}

/// The path to navigate to for a parsed link. When the answer cited several moments in the same recording,
/// pass them all as `allTimes` — they ride along as `ts=` so the transcript can offer prev/next navigation,
/// with the clicked moment (or the first) as the active `t`.
export function recordingLinkPath(link: RecordingLink, allTimes?: number[]): string {
  const params = new URLSearchParams();
  const active = link.t ?? (allTimes && allTimes.length > 0 ? allTimes[0] : null);
  if (active != null) params.set("t", String(active));
  if (allTimes && allTimes.length > 1) params.set("ts", allTimes.join(","));
  const qs = params.toString();
  return qs ? `/recordings/${link.id}?${qs}` : `/recordings/${link.id}`;
}

/// Parses the `ts` query value (a comma-separated list of ms positions) into a sorted, de-duplicated list.
export function parseMatchTimes(raw: string | null): number[] {
  if (!raw) return [];
  const nums = raw
    .split(",")
    .map((s) => s.trim())
    .filter((s) => /^\d+$/.test(s))
    .map(Number);
  return [...new Set(nums)].sort((a, b) => a - b);
}

/// The index of the segment that contains `ms` (start ≤ ms < end), else the nearest by start time, else -1.
export function segmentIndexAtMs(segs: ReadonlyArray<{ startMs: number; endMs: number }>, ms: number): number {
  const exact = segs.findIndex((s) => ms >= s.startMs && ms < s.endMs);
  if (exact >= 0) return exact;
  let best = -1;
  let bestDist = Infinity;
  segs.forEach((s, i) => {
    const d = Math.abs(s.startMs - ms);
    if (d < bestDist) {
      bestDist = d;
      best = i;
    }
  });
  return best;
}
