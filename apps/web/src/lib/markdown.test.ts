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
});
