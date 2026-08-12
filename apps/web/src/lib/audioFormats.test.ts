import { describe, it, expect } from "vitest";
import { fileExtension, titleFromFilename } from "./audioFormats";

describe("audioFormats", () => {
  it("extracts a lower-cased extension", () => {
    expect(fileExtension("Memo.WAV")).toBe("wav");
    expect(fileExtension("a.b.mp3")).toBe("mp3");
    expect(fileExtension("noext")).toBe("");
  });

  it("derives a title from the filename", () => {
    expect(titleFromFilename("Team Standup.m4a")).toBe("Team Standup");
    expect(titleFromFilename(".mp3")).toBe("Uploaded audio"); // no base name
  });
});
