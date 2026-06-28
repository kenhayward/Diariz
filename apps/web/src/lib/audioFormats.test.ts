import { describe, it, expect } from "vitest";
import {
  fileExtension,
  isAcceptedAudioFilename,
  titleFromFilename,
  precheckUpload,
  AUDIO_ACCEPT_ATTR,
} from "./audioFormats";

describe("audioFormats", () => {
  it("extracts a lower-cased extension", () => {
    expect(fileExtension("Memo.WAV")).toBe("wav");
    expect(fileExtension("a.b.mp3")).toBe("mp3");
    expect(fileExtension("noext")).toBe("");
  });

  it("accepts known audio extensions, rejects others", () => {
    for (const ok of ["a.wav", "a.mp3", "a.flac", "a.ogg", "a.opus", "a.webm", "a.m4a"])
      expect(isAcceptedAudioFilename(ok)).toBe(true);
    for (const bad of ["a.txt", "a.exe", "a.pdf", "noext"])
      expect(isAcceptedAudioFilename(bad)).toBe(false);
  });

  it("derives a title from the filename", () => {
    expect(titleFromFilename("Team Standup.m4a")).toBe("Team Standup");
    expect(titleFromFilename(".mp3")).toBe("Uploaded audio"); // no base name
  });

  it("precheck flags type, emptiness, and size", () => {
    expect(precheckUpload({ name: "ok.wav", size: 100 })).toBeNull();
    expect(precheckUpload({ name: "bad.txt", size: 100 })).toMatch(/Unsupported/);
    expect(precheckUpload({ name: "empty.wav", size: 0 })).toMatch(/empty/);
    expect(precheckUpload({ name: "big.wav", size: 50 }, 10)).toMatch(/too large/i);
  });

  it("exposes an accept attribute covering the formats", () => {
    expect(AUDIO_ACCEPT_ATTR).toContain(".wav");
    expect(AUDIO_ACCEPT_ATTR).toContain(".m4a");
    expect(AUDIO_ACCEPT_ATTR).toContain("audio/*");
  });
});
