import { describe, it, expect } from "vitest";
import { renderMarkdown } from "./markdown";

describe("renderMarkdown", () => {
  it("renders markdown to HTML", () => {
    expect(renderMarkdown("**bold**")).toContain("<strong>bold</strong>");
  });

  it("sanitizes script tags out of model output", () => {
    const html = renderMarkdown("hello <script>alert(1)</script> there");
    expect(html).not.toContain("<script>");
    expect(html).toContain("hello");
    expect(html).toContain("there");
  });

  it("handles empty input", () => {
    expect(renderMarkdown("")).toBe("");
  });

  it("treats a single newline as a line break, for chat and model output", () => {
    expect(renderMarkdown("one\ntwo")).toContain("<br>");
  });

  it("keeps a hard-wrapped paragraph flowing when breaks are off", () => {
    // Authored documents (help articles) are wrapped in the source for readable diffs; a `<br>` per
    // source line would break them into ragged short lines.
    const html = renderMarkdown("one\ntwo", { breaks: false });
    expect(html).not.toContain("<br>");
    expect(html).toContain("one\ntwo");
  });

  it("still separates paragraphs on a blank line when breaks are off", () => {
    const html = renderMarkdown("one\n\ntwo", { breaks: false });
    expect(html.match(/<p>/g)).toHaveLength(2);
  });

  it("sanitizes with breaks off too", () => {
    expect(renderMarkdown("<script>alert(1)</script>", { breaks: false })).not.toContain("<script>");
  });
});
