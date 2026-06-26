import { marked } from "marked";
import DOMPurify from "dompurify";

/// Render assistant markdown to sanitized HTML. The model's output is untrusted, so always run it
/// through DOMPurify before injecting via dangerouslySetInnerHTML.
export function renderMarkdown(text: string): string {
  const html = marked.parse(text ?? "", { async: false, breaks: true }) as string;
  return DOMPurify.sanitize(html);
}
