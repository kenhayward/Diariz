import type { SegmentDto } from "./types";

/** True when any segment carries a revision (a user edit or translation). A blank revision ("") still
 * counts — it is a deliberate edit, distinct from "no revision" (null/undefined). */
export function hasRevisions(segments: SegmentDto[]): boolean {
  return segments.some((s) => s.revised != null);
}

/** The text to show for a segment: the model's original when `showOriginal`, else the effective text
 * (the revision when present, else the original). */
export function segmentText(seg: SegmentDto, showOriginal: boolean): string {
  return showOriginal ? seg.original : seg.text;
}

/** Label for the Show Original / Show Revised toggle — it offers the *other* view. */
export function toggleLabel(showOriginal: boolean): string {
  return showOriginal ? "Show revised" : "Show original";
}
