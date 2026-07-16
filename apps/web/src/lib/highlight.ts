/// Splitting a snippet around the searched term, so the UI can mark the matches.
///
/// The server sends snippets as **plain text** and never as markup - highlighting is the client's job, and
/// doing it by slicing strings (rather than injecting `<mark>` into HTML) means a transcript containing
/// something that looks like a tag stays text.

export interface HighlightPart {
  text: string;
  match: boolean;
}

/// Escape the query for use inside a RegExp. It is raw user input: an unescaped `(` throws, and `.*` would
/// silently match the whole snippet.
function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/// Trim a snippet down to roughly `maxChars` **around the match**, so the matched words are always the part
/// you can see.
///
/// A search row is one truncated line. The server's snippet is a whole transcript segment, and the match is
/// often at the end of it - so plain CSS truncation clips off the very words you searched for, leaving a
/// result that never explains itself. Windowing here means the ellipsis falls on the boring end instead.
/// Dropped text is marked with a leading/trailing "…". Falls back to the start of the text when the query
/// isn't found (a semantic hit matched by meaning, not by keyword - there is nothing to centre on).
export function snippetWindow(text: string, query: string, maxChars = 90): string {
  if (text.length <= maxChars) return text;

  const needle = query.trim();
  const at = needle.length === 0 ? -1 : text.toLowerCase().indexOf(needle.toLowerCase());
  if (at < 0) return text.slice(0, maxChars).trimEnd() + "…";

  // Keep a little run-up so the match reads in context rather than starting the line.
  const lead = Math.floor((maxChars - needle.length) / 3);
  let start = Math.max(0, at - Math.max(lead, 0));
  let end = Math.min(text.length, start + maxChars);

  // Snap to word boundaries so the window reads as a trimmed quote rather than a rendering bug ("…w smart").
  // Never snap past the match itself - a long word can be wider than the window, and showing the match beats
  // showing whole words.
  if (start > 0) {
    const space = text.indexOf(" ", start);
    if (space >= 0 && space < at) start = space + 1;
  }
  if (end < text.length) {
    const space = text.lastIndexOf(" ", end);
    if (space > at + needle.length) end = space;
  }

  return (start > 0 ? "…" : "") + text.slice(start, end).trim() + (end < text.length ? "…" : "");
}

/// `text` split into alternating unmatched/matched parts, case-insensitively. Concatenating the parts always
/// reproduces `text` exactly, with the original casing. An empty query yields the text unmarked.
export function highlight(text: string, query: string): HighlightPart[] {
  const needle = query.trim();
  if (needle.length === 0 || text.length === 0) return [{ text, match: false }];

  const parts: HighlightPart[] = [];
  const re = new RegExp(escapeRegExp(needle), "gi");
  let last = 0;
  for (let m = re.exec(text); m !== null; m = re.exec(text)) {
    if (m.index > last) parts.push({ text: text.slice(last, m.index), match: false });
    parts.push({ text: m[0], match: true });
    last = m.index + m[0].length;
    if (m[0].length === 0) re.lastIndex++; // a zero-width match would loop forever
  }
  if (last < text.length) parts.push({ text: text.slice(last), match: false });
  return parts;
}
