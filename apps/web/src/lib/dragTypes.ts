/// Drag payload types used by the recordings panel. A drag carries exactly one of these:
/// - `SECTION_MIME` - a folder being reordered or reparented.
/// - `"text/plain"` - a recording id being reordered.
/// - `"Files"` - an OS file drag, which the panel turns into an upload.
///
/// Lives here rather than in a component so the panel and the nav rows can share it without importing
/// each other.
export const SECTION_MIME = "application/x-diariz-section";
