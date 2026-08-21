import type { DragEvent } from "react";

/// Drag payload types used by the recordings panel. A drag carries exactly one of these:
/// - `SECTION_MIME` - a folder being reordered or reparented.
/// - `"text/plain"` - a recording id being reordered.
/// - `"Files"` - an OS file drag, which the panel turns into an upload.
///
/// Lives here rather than in a component so the panel and the nav rows can share it without importing
/// each other.
export const SECTION_MIME = "application/x-diariz-section";

/// A meeting screenshot being dragged from a recording's Notes tab into the chat prompt. Its own type so
/// the composer cannot mistake an arbitrary dragged word or link for a capture.
export const SCREENSHOT_DRAG_TYPE = "application/x-diariz-screenshot";

/// Whether a drag is carrying OS files (an upload) rather than one of the app's own drag payloads.
/// The same drop handlers see both, so this is what keeps a folder reorder from being read as an upload.
/// `types` is guaranteed by the spec but not by every jsdom/browser shim, hence the `?? []`.
///
/// **A screenshot drag is excluded even though it advertises `Files`.** Dragging one of our thumbnails
/// means dragging an `<img>`, and Chrome adds `Files` to an image drag because it can be dropped into the
/// OS as a file. Without this exclusion every upload drop zone lit up as though a file were incoming; and
/// since the drop then landed on the chat composer, the zone saw neither a drop nor a matching dragleave
/// and stayed highlighted indefinitely. Keying on our own type rather than on "an image is involved"
/// keeps a genuine PNG dragged in from the desktop working as an upload.
export function dragHasFiles(e: DragEvent): boolean {
  const types = Array.from(e.dataTransfer.types ?? []);
  return types.includes("Files") && !types.includes(SCREENSHOT_DRAG_TYPE);
}
