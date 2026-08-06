import type { DragEvent } from "react";

/// Drag payload types used by the recordings panel. A drag carries exactly one of these:
/// - `SECTION_MIME` - a folder being reordered or reparented.
/// - `"text/plain"` - a recording id being reordered.
/// - `"Files"` - an OS file drag, which the panel turns into an upload.
///
/// Lives here rather than in a component so the panel and the nav rows can share it without importing
/// each other.
export const SECTION_MIME = "application/x-diariz-section";

/// Whether a drag is carrying OS files (an upload) rather than one of the panel's own reorder payloads.
/// The same drop handlers see both, so this is what keeps a folder reorder from being read as an upload.
/// `types` is guaranteed by the spec but not by every jsdom/browser shim, hence the `?? []`.
export function dragHasFiles(e: DragEvent): boolean {
  return Array.from(e.dataTransfer.types ?? []).includes("Files");
}
