import { describe, it, expect } from "vitest";
import { linkifyRecordings } from "./linkify";

const ref = { name: "Acme Sync", href: "/recordings/abc" };

describe("linkifyRecordings", () => {
  it("links the first plain mention of a referenced recording", () => {
    const out = linkifyRecordings("We discussed pricing in Acme Sync last week.", [ref]);
    expect(out).toBe("We discussed pricing in [Acme Sync](/recordings/abc) last week.");
  });

  it("leaves it alone when the model already linked it", () => {
    const md = "See [Acme Sync](/recordings/abc?t=1000) for details.";
    expect(linkifyRecordings(md, [ref])).toBe(md);
  });

  it("only links the first occurrence", () => {
    const out = linkifyRecordings("Acme Sync and again Acme Sync.", [ref]);
    expect(out).toBe("[Acme Sync](/recordings/abc) and again Acme Sync.");
  });

  it("does nothing when the name isn't mentioned", () => {
    expect(linkifyRecordings("Nothing relevant here.", [ref])).toBe("Nothing relevant here.");
  });

  it("ignores very short names to avoid noise", () => {
    const out = linkifyRecordings("Q3 was good.", [{ name: "Q3", href: "/recordings/x" }]);
    expect(out).toBe("Q3 was good.");
  });

  it("does not link inside a word", () => {
    const out = linkifyRecordings("Acme Syncing is not the meeting.", [ref]);
    expect(out).toBe("Acme Syncing is not the meeting.");
  });
});
