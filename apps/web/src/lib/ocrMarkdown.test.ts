import { describe, it, expect } from "vitest";
import { ocrToMarkdown } from "./ocrMarkdown";

describe("ocrToMarkdown", () => {
  /// The common case by far. Plain text must come back byte-identical - running a converter over output
  /// that needs no conversion is the easiest way to introduce a regression nobody looks for.
  it("leaves plain text completely alone", () => {
    const text = "Manufacturability Evaluation\n\nWork ongoing to improve manufacturability";
    expect(ocrToMarkdown(text)).toBe(text);
  });

  it("converts an HTML table to a GFM table", () => {
    const html =
      "<table><tr><th>Core requirement</th><th>Total</th></tr>" +
      "<tr><td>General Requirement</td><td>38</td></tr>" +
      "<tr><td>Cells &amp; Seeds</td><td>15</td></tr></table>";

    expect(ocrToMarkdown(html)).toBe(
      [
        "| Core requirement | Total |",
        "| --- | --- |",
        "| General Requirement | 38 |",
        "| Cells & Seeds | 15 |",
      ].join("\n"),
    );
  });

  /// GFM has no table without a header row, and the models do not reliably emit `<th>`. Promoting the
  /// first row is what the reader would do looking at the rendered original.
  it("promotes the first row to the header when there are no th cells", () => {
    const html = "<table><tr><td>Version</td><td>Date</td></tr><tr><td>1.0</td><td>30 oct. 2025</td></tr></table>";

    expect(ocrToMarkdown(html)).toBe(
      ["| Version | Date |", "| --- | --- |", "| 1.0 | 30 oct. 2025 |"].join("\n"),
    );
  });

  /// `<br>` is the accepted GFM idiom for a line break inside a table cell - a real newline would end the
  /// row. The document-history tables this was built for lean on it heavily.
  it("keeps br as a line break inside a cell", () => {
    const html =
      "<table><tr><th>Change</th></tr><tr><td>Responsibilities relocated<br>Addition of the Launch roles</td></tr></table>";

    expect(ocrToMarkdown(html)).toContain("| Responsibilities relocated<br>Addition of the Launch roles |");
  });

  /// A pipe inside a cell would otherwise split it into two, silently shifting every later column.
  it("escapes a pipe inside a cell", () => {
    const html = "<table><tr><th>A</th></tr><tr><td>red | amber</td></tr></table>";

    expect(ocrToMarkdown(html)).toContain("| red \\| amber |");
  });

  /// One model rendered gauge graphics as `<img src="green.png" alt="Green">`. The alt text is the only
  /// part with any meaning, and the src points at a file that does not exist.
  it("reduces an img to its alt text", () => {
    const html = '<table><tr><th>Scoring</th></tr><tr><td><img src="green.png" alt="Green"></td></tr></table>';

    expect(ocrToMarkdown(html)).toContain("| Green |");
  });

  it("empties an img that has no alt text", () => {
    const html = '<table><tr><th>Scoring</th></tr><tr><td><img src="green.png"></td></tr></table>';

    expect(ocrToMarkdown(html)).toContain("|  |");
  });

  /// Ragged rows are common: a model drops a trailing empty cell. Padding keeps the columns aligned
  /// instead of producing a table a renderer refuses.
  it("pads a short row to the header's width", () => {
    const html = "<table><tr><th>A</th><th>B</th><th>C</th></tr><tr><td>1</td></tr></table>";

    expect(ocrToMarkdown(html)).toContain("| 1 |  |  |");
  });

  it("keeps text either side of the table", () => {
    const html = "Table 28: Document history\n\n<table><tr><th>A</th></tr><tr><td>1</td></tr></table>\n\nEnd note";
    const out = ocrToMarkdown(html);

    expect(out.startsWith("Table 28: Document history")).toBe(true);
    expect(out.trimEnd().endsWith("End note")).toBe(true);
    expect(out).toContain("| --- |");
  });

  it("converts several tables in one document", () => {
    const html =
      "<table><tr><th>A</th></tr><tr><td>1</td></tr></table>" +
      "<table><tr><th>B</th></tr><tr><td>2</td></tr></table>";
    const out = ocrToMarkdown(html);

    expect(out).toContain("| A |");
    expect(out).toContain("| B |");
    expect(out.match(/\| --- \|/g)).toHaveLength(2);
  });

  it("decodes the entities a model emits", () => {
    expect(ocrToMarkdown("<p>Mammalian &amp; Microbial &quot;scope&quot;&nbsp;here</p>"))
      .toBe('Mammalian & Microbial "scope" here');
  });

  /// Angle brackets stay escaped rather than decoding to raw markup. Two reasons, and the second is the one
  /// that bites a reader: a raw `<non-vaccines>` is swallowed by a Markdown renderer as an unknown HTML
  /// tag, so the text simply disappears. This test previously asserted the opposite and was wrong.
  it("keeps decoded angle brackets escaped so the text survives rendering", () => {
    expect(ocrToMarkdown("<p>&lt;non-vaccines&gt;</p>")).toBe("&lt;non-vaccines&gt;");
  });

  /// Stray non-table markup should not survive into a Markdown file, but its text must.
  it("strips non-table tags while keeping their text", () => {
    expect(ocrToMarkdown("<p>Core principles</p><div>Scoring system</div>")).toContain("Core principles");
    expect(ocrToMarkdown("<p>Core principles</p>")).not.toContain("<p>");
  });

  /// Whitespace inside a cell is meaningless in the source and fatal to alignment in the output.
  it("collapses whitespace inside cells", () => {
    const html = "<table><tr><th>A</th></tr><tr><td>\n   spread   over\n   lines\n</td></tr></table>";

    expect(ocrToMarkdown(html)).toContain("| spread over lines |");
  });

  /// A truncated response is a real possibility at a token cap, and must not throw inside a click handler.
  it("does not throw on an unclosed table", () => {
    expect(() => ocrToMarkdown("<table><tr><td>1</td></tr>")).not.toThrow();
  });

  it("handles an empty or blank input", () => {
    expect(ocrToMarkdown("")).toBe("");
    expect(ocrToMarkdown("   ")).toBe("   ");
  });

  /// Attribute noise varies between models; matching must not depend on it.
  it("tolerates attributes and mixed case on the tags", () => {
    const html = '<TABLE class="x"><TR><TH scope="col">A</TH></TR><TR><TD>1</TD></TR></TABLE>';

    expect(ocrToMarkdown(html)).toContain("| A |");
    expect(ocrToMarkdown(html)).toContain("| 1 |");
  });
});

/// The defects CodeQL raised against this file (alerts 25-27), each reproduced as the caller would hit it.
///
/// The output is not an XSS sink today - both destinations render through `renderMarkdown`, which runs
/// DOMPurify - but this function's contract is that its output carries no markup except the `<br>` it
/// deliberately emits, and relying on a downstream sanitizer to make that true is not the same as it being
/// true.
describe("ocrToMarkdown - markup and escaping", () => {
  /// The strip used to run BEFORE the decode, so entity-encoded markup passed through as plain text and
  /// then decoded into a live element on the way out.
  it("does not let entity-encoded markup decode into live markup", () => {
    const out = ocrToMarkdown("&lt;script&gt;alert(1)&lt;/script&gt;");

    expect(out).not.toContain("<script>");
    expect(out).not.toContain("</script>");
  });

  it("does not let entity-encoded markup through inside a table cell", () => {
    const out = ocrToMarkdown("<table><tr><th>A</th></tr><tr><td>&lt;img src=x onerror=alert(1)&gt;</td></tr></table>");

    expect(out).not.toContain("<img");
  });

  /// Belt and braces on the strip itself. This regex consumes from a `<` to the next `>`, so a surviving
  /// `<` cannot form a complete element - but the strip should be stable under repetition regardless,
  /// rather than depending on that argument staying true if the pattern is ever edited.
  it("leaves nothing that a second strip would still find", () => {
    for (const attack of [
      "<scr<script>ipt>alert(1)</scr</script>ipt>",
      "<<script>script>alert(1)<</script>/script>",
      "<img sr<x>c=x onerror=alert(1)>",
    ]) {
      const out = ocrToMarkdown(attack);
      expect(out).not.toContain("<script");
      expect(out).not.toContain("<img");
    }
  });

  /// The `<br>` this file emits itself is the one piece of markup that must survive - it is the only line
  /// break a GFM cell allows.
  it("still emits its own br inside a cell", () => {
    expect(ocrToMarkdown("<table><tr><th>A</th></tr><tr><td>one<br>two</td></tr></table>"))
      .toContain("| one<br>two |");
  });

  /// A backslash already in the text was not escaped, so `\|` became `\|` - a literal backslash followed
  /// by a live delimiter, splitting the cell and shifting every column after it. Backslashes are common in
  /// exactly what this reads: file paths, regexes, terminal output.
  it("escapes a backslash so it cannot un-escape the pipe after it", () => {
    const backslashPipe = "<table><tr><th>A</th><th>B</th></tr><tr><td>x" + String.fromCharCode(92) + "|y</td><td>z</td></tr></table>";
    const out = ocrToMarkdown(backslashPipe);

    // The backslash is escaped in its own right, so the pipe after it is still escaped by ITS backslash
    // rather than being un-escaped by the one in the text.
    expect(out).toContain(String.raw`| x\\\|y | z |`);
  });

  it("keeps a Windows path intact in a cell", () => {
    const path = "C:" + String.fromCharCode(92) + "Users" + String.fromCharCode(92) + "me";
    const out = ocrToMarkdown("<table><tr><th>Path</th></tr><tr><td>" + path + "</td></tr></table>");

    // Each backslash is escaped for Markdown, so the path renders as it was captured rather than losing
    // its separators to the renderer.
    expect(out).toContain(String.raw`C:\\Users\\me`);
  });
});
