/// Turns the HTML an OCR model emits into Markdown, so extracted text renders as text rather than as
/// angle brackets.
///
/// **Why this is needed at all.** Asked to read a page, the models that do this best return *structure* -
/// a document with a table in it comes back as `<table><tr><th>...`, and one model rendered gauge graphics
/// as `<img src="green.png" alt="Green">`. That is genuinely more useful than flat lines, and not something
/// to suppress with a stricter prompt: prompting one model away from HTML narrowed it to a single region
/// and discarded the rest of the capture. So the structure is worth keeping, and worth translating.
///
/// **Why here and not on the server.** The API caches the model's answer verbatim on the capture, and that
/// is deliberate - it is the record of what the model actually said, which matters for a feature whose
/// output carries an "unverified" stamp. Conversion belongs at the point the text becomes a Markdown note
/// or a chat context, which is here.
///
/// **Deliberately not a general HTML-to-Markdown library.** The input is not arbitrary web HTML; it is the
/// narrow output of an OCR model, which in practice means tables, `<br>`, `<img>`, entities, and the
/// occasional stray block tag. A dependency for that would be a lot of behaviour to reason about for a
/// small, well-understood job - and it would still need the GFM-specific handling below (pipe escaping,
/// the synthesised header row, ragged-row padding) written by hand on top.
///
/// Everything here is pure and string-only: no DOM, so it runs the same in a test as in the browser.

/// The entities a model actually emits. Not a full table on purpose - a numeric-entity decoder would be
/// more code and more risk than the handful of named ones that turn up.
const ENTITIES: Record<string, string> = {
  "&amp;": "&",
  "&lt;": "<",
  "&gt;": ">",
  "&quot;": '"',
  "&#39;": "'",
  "&apos;": "'",
  "&nbsp;": " ",
};

/// Stand-in for a line break inside a table cell, held from the moment a cell is flattened until the very
/// last step of the conversion.
///
/// It has to be a character that cannot appear in the source, and it has to survive three passes that
/// would otherwise destroy it: the tag-strip, the whitespace-collapse, and - the one that actually caught
/// this out - the document-wide `<br>`-to-newline pass at the end, which would happily convert a `<br>`
/// this file had just produced and break the table row it sits in.
const BREAK = "\u0001";
const BREAK_PADDED = new RegExp(`\\s*${BREAK}\\s*`, "g");

function decodeEntities(text: string): string {
  // One pass rather than one replace per entity: doing &amp; last would double-decode "&amp;lt;" into "<".
  return text.replace(/&(amp|lt|gt|quot|apos|nbsp|#39);/g, (m) => ENTITIES[m] ?? m);
}

/// One cell's text: images reduced to their alt, breaks parked, every other tag dropped, whitespace
/// collapsed, and pipes escaped so a cell containing one cannot silently become two columns.
function cellText(html: string): string {
  return decodeEntities(
    html
      .replace(/<img\b[^>]*>/gi, (tag) => {
        const alt = /\balt\s*=\s*("([^"]*)"|'([^']*)'|([^\s>]+))/i.exec(tag);
        return alt ? (alt[2] ?? alt[3] ?? alt[4] ?? "") : "";
      })
      .replace(/<br\s*\/?>/gi, BREAK)
      .replace(/<[^>]*>/g, ""),
  )
    // Collapses runs of real whitespace. The parked break is not whitespace, so it survives.
    .replace(/\s+/g, " ")
    .trim()
    .replace(/\|/g, "\\|")
    // Whitespace either side of a break came from the source's indentation, not from the content.
    .replace(BREAK_PADDED, BREAK);
}

/// One `<table>` as a GFM table, or null when it holds no rows at all - in which case the caller drops it
/// rather than emitting a header-only skeleton.
function tableToMarkdown(tableHtml: string): string | null {
  const rows = [...tableHtml.matchAll(/<tr\b[^>]*>([\s\S]*?)<\/tr\s*>/gi)]
    .map((m) => [...m[1].matchAll(/<(td|th)\b[^>]*>([\s\S]*?)<\/\1\s*>/gi)].map((c) => cellText(c[2])))
    .filter((r) => r.length > 0);

  if (rows.length === 0) return null;

  // GFM has no table without a header row. A `<th>` row is the header when there is one; otherwise the
  // first row is promoted, which is what a reader looking at the rendered original would do anyway.
  const [header, ...body] = rows;
  // Ragged rows are common - a model drops a trailing empty cell - and a short row makes a table a
  // renderer will refuse, so every row is padded to the widest.
  const width = Math.max(...rows.map((r) => r.length));
  const pad = (r: string[]) => [...r, ...Array(width - r.length).fill("")];
  const line = (r: string[]) => `| ${pad(r).join(" | ")} |`;

  return [line(header), `| ${Array(width).fill("---").join(" | ")} |`, ...body.map(line)].join("\n");
}

/// Convert an OCR model's answer to Markdown.
///
/// Text with no markup in it is returned **byte-identical** - the overwhelmingly common case, and the one
/// where a converter can only do harm.
export function ocrToMarkdown(text: string): string {
  if (!text || !/[<&]/.test(text)) return text;

  // Tables first, so their internals are never touched by the passes below.
  let out = text.replace(/<table\b[^>]*>[\s\S]*?<\/table\s*>/gi, (table) => {
    const md = tableToMarkdown(table);
    return md === null ? "" : `\n\n${md}\n\n`;
  });

  // An unclosed table - a reply truncated at its token cap - does not match above, so its rows would
  // survive as raw tags. Convert what is there rather than leave markup in a Markdown file.
  if (/<table\b/i.test(out)) {
    out = out.replace(/<table\b[^>]*>[\s\S]*$/i, (rest) => {
      const md = tableToMarkdown(rest);
      return md === null ? "" : `\n\n${md}\n`;
    });
  }

  out = decodeEntities(
    out
      .replace(/<br\s*\/?>/gi, "\n")
      .replace(/<\/(p|div|h[1-6]|li|tr)\s*>/gi, "\n")
      .replace(/<[^>]*>/g, ""),
  );

  // Only now do parked breaks become `<br>` - after the pass above, which would otherwise turn them into
  // newlines and split the table rows they belong to. `<br>` is the accepted GFM idiom for a line break
  // inside a cell; a real newline there would end the row.
  return out
    .replace(new RegExp(BREAK, "g"), "<br>")
    // The table replacement pads with blank lines to keep each table a block of its own; three or more in
    // a row is just a gap nobody asked for.
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}
