import type { Attachment, SectionAttachmentItem } from "./types";

/// Is this attachment editable Markdown - a `text/markdown` file or a `.md/.markdown/.mdown` name?
/// Mirrors the server rule in `Services/MarkdownAttachments.cs` (gates the in-place edit endpoint).
export function isMarkdownAttachment(a: Attachment | SectionAttachmentItem): boolean {
  return (
    a.kind === "File" &&
    (a.contentType === "text/markdown" || /\.(md|markdown|mdown)$/i.test(a.name))
  );
}
