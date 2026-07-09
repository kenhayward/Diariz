import { describe, it, expect } from "vitest";
import { isMarkdownAttachment } from "./attachments";
import type { Attachment } from "./types";

const base: Attachment = { id: "1", kind: "File", name: "x", contentType: null, sizeBytes: 0, url: null, ordinal: 0 };

describe("isMarkdownAttachment", () => {
  it("is true for a text/markdown content type", () => {
    expect(isMarkdownAttachment({ ...base, name: "note", contentType: "text/markdown" })).toBe(true);
  });

  it("is true for a .md / .markdown / .mdown name", () => {
    expect(isMarkdownAttachment({ ...base, name: "summary.md" })).toBe(true);
    expect(isMarkdownAttachment({ ...base, name: "notes.MARKDOWN" })).toBe(true);
    expect(isMarkdownAttachment({ ...base, name: "readme.mdown" })).toBe(true);
  });

  it("is false for other files and for URLs", () => {
    expect(isMarkdownAttachment({ ...base, name: "spec.pdf", contentType: "application/pdf" })).toBe(false);
    expect(isMarkdownAttachment({ ...base, kind: "Url", name: "link.md", url: "https://x.test" })).toBe(false);
  });
});
