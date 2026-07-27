import { marked } from "marked";
import DOMPurify from "dompurify";

/// Render markdown to sanitized HTML. The model's output is untrusted, so always run it through
/// DOMPurify before injecting via dangerouslySetInnerHTML.
///
/// `breaks` defaults to true, which is right for chat and model output: there a single newline is meant
/// as a line break. Pass `false` for **authored** documents (the help articles), whose source is
/// hard-wrapped for readable diffs - otherwise every wrapped line becomes a `<br>` and paragraphs render
/// as ragged short lines.
export function renderMarkdown(text: string, { breaks = true }: { breaks?: boolean } = {}): string {
  const html = marked.parse(text ?? "", { async: false, breaks }) as string;
  return DOMPurify.sanitize(html);
}
