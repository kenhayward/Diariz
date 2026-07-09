import { describe, it, expect, vi, afterEach } from "vitest";
import { copyRichLink, transcriptUrl, folderUrl } from "./clipboard";

afterEach(() => {
  vi.unstubAllGlobals();
});

// jsdom's Blob has no .text(); stub a minimal one that records its parts so tests can read them back.
class FakeBlob {
  constructor(public parts: string[]) {}
  text() {
    return Promise.resolve(this.parts.join(""));
  }
}

describe("transcriptUrl", () => {
  it("builds an absolute /recordings/:id link on the current origin", () => {
    expect(transcriptUrl("abc-123")).toBe(`${window.location.origin}/recordings/abc-123`);
  });
});

describe("folderUrl", () => {
  it("builds an absolute /sections/:id link on the current origin", () => {
    expect(folderUrl("f-1")).toBe(`${window.location.origin}/sections/f-1`);
  });
});

describe("copyRichLink", () => {
  it("writes both an HTML anchor (name as link text) and a plain-text URL", async () => {
    const write = vi.fn().mockResolvedValue(undefined);
    // A minimal ClipboardItem stub that records the data it was given.
    class FakeClipboardItem {
      constructor(public items: Record<string, Blob>) {}
    }
    vi.stubGlobal("Blob", FakeBlob);
    vi.stubGlobal("ClipboardItem", FakeClipboardItem);
    vi.stubGlobal("navigator", { clipboard: { write, writeText: vi.fn() } });

    const ok = await copyRichLink("https://x.test/recordings/1", "Team Sync");

    expect(ok).toBe(true);
    const item = write.mock.calls[0][0][0] as unknown as FakeClipboardItem;
    const html = await item.items["text/html"].text();
    const plain = await item.items["text/plain"].text();
    expect(html).toBe('<a href="https://x.test/recordings/1">Team Sync</a>');
    expect(plain).toBe("https://x.test/recordings/1");
  });

  it("escapes HTML in the URL and label", async () => {
    const write = vi.fn().mockResolvedValue(undefined);
    class FakeClipboardItem {
      constructor(public items: Record<string, Blob>) {}
    }
    vi.stubGlobal("Blob", FakeBlob);
    vi.stubGlobal("ClipboardItem", FakeClipboardItem);
    vi.stubGlobal("navigator", { clipboard: { write, writeText: vi.fn() } });

    await copyRichLink("https://x.test/r", 'A & B <b>"x"</b>');

    const item = write.mock.calls[0][0][0] as unknown as FakeClipboardItem;
    const html = await item.items["text/html"].text();
    expect(html).toBe('<a href="https://x.test/r">A &amp; B &lt;b&gt;&quot;x&quot;&lt;/b&gt;</a>');
  });

  it("falls back to writeText when the rich clipboard API is unavailable", async () => {
    vi.stubGlobal("ClipboardItem", undefined);
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });

    const ok = await copyRichLink("https://x.test/r", "Name");

    expect(ok).toBe(true);
    expect(writeText).toHaveBeenCalledWith("https://x.test/r");
  });
});
