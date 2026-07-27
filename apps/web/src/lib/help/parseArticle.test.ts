import { describe, it, expect } from "vitest";
import { parseArticle } from "./parseArticle";

const raw = `---
title: Recording a meeting
summary: Capture from your microphone, system audio, or both.
group: getting-started
order: 20
---

## Starting a recording

Press the record button.
`;

describe("parseArticle", () => {
  it("reads the front-matter fields", () => {
    const a = parseArticle("recording-audio", "en", raw);
    expect(a.slug).toBe("recording-audio");
    expect(a.locale).toBe("en");
    expect(a.title).toBe("Recording a meeting");
    expect(a.summary).toBe("Capture from your microphone, system audio, or both.");
    expect(a.group).toBe("getting-started");
    expect(a.order).toBe(20);
  });

  it("keeps the body verbatim, without the front-matter block", () => {
    const a = parseArticle("recording-audio", "en", raw);
    expect(a.body).toBe("## Starting a recording\n\nPress the record button.\n");
  });

  it("keeps a colon in the value", () => {
    const a = parseArticle("x", "en", "---\ntitle: Chat: ask anything\n---\nbody");
    expect(a.title).toBe("Chat: ask anything");
  });

  it("trims surrounding whitespace from keys and values", () => {
    const a = parseArticle("x", "en", "---\n  title  :   Spaced   \n---\nbody");
    expect(a.title).toBe("Spaced");
  });

  it("defaults order to a large number so unordered articles sort last", () => {
    const a = parseArticle("x", "en", "---\ntitle: T\n---\nbody");
    expect(a.order).toBe(999);
  });

  it("falls back to the slug as the title when there is no front matter", () => {
    const a = parseArticle("some-slug", "en", "just body text");
    expect(a.title).toBe("some-slug");
    expect(a.summary).toBe("");
    expect(a.body).toBe("just body text");
  });

  it("ignores blank lines and comment lines inside the block", () => {
    const a = parseArticle("x", "en", "---\n\n# a comment\ntitle: T\n---\nbody");
    expect(a.title).toBe("T");
  });

  it("tolerates CRLF line endings", () => {
    const a = parseArticle("x", "en", "---\r\ntitle: T\r\ngroup: g\r\n---\r\nbody\r\n");
    expect(a.title).toBe("T");
    expect(a.group).toBe("g");
    expect(a.body).toBe("body\n");
  });

  it("puts an article with no group into the fallback group", () => {
    const a = parseArticle("x", "en", "---\ntitle: T\n---\nbody");
    expect(a.group).toBe("other");
  });
});
