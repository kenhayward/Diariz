import { describe, it, expect } from "vitest";
import {
  classifyFile,
  sourceProblem,
  resultProblem,
  MEDIA_ACCEPT_ATTR,
  MAX_SOURCE_BYTES,
} from "./mediaKinds";

describe("mediaKinds", () => {
  it("classifies audio formats as pass-through", () => {
    for (const name of ["a.wav", "a.mp3", "a.flac", "a.ogg", "a.opus", "a.m4a", "a.aac"])
      expect(classifyFile({ name }), name).toBe("audio");
  });

  it("classifies containers that may hold video, including .webm", () => {
    for (const name of ["a.mp4", "a.m4v", "a.mov", "a.mkv", "a.webm"])
      expect(classifyFile({ name }), name).toBe("container");
  });

  it("rejects formats we cannot demux", () => {
    for (const name of ["a.avi", "a.wmv", "a.flv", "a.txt", "noext"])
      expect(classifyFile({ name }), name).toBe("rejected");
  });

  it("is case-insensitive", () => {
    expect(classifyFile({ name: "Town Hall.MP4" })).toBe("container");
    expect(classifyFile({ name: "Memo.WAV" })).toBe("audio");
  });

  // The regression this whole task exists for: a multi-GB video must survive the source gate that a
  // multi-GB *upload* would fail, because extraction has not turned it into ~52 MB yet.
  it("lets a 2.8 GB video past the source guard", () => {
    expect(sourceProblem({ name: "Q3 Town Hall.mp4", size: 2_800_000_000 })).toBeNull();
  });

  it("flags an unsupported type, an empty file, and an absurd source", () => {
    expect(sourceProblem({ name: "clip.avi", size: 100 })).toMatch(/convert/i);
    expect(sourceProblem({ name: "empty.mp4", size: 0 })).toMatch(/empty/i);
    expect(sourceProblem({ name: "huge.mp4", size: MAX_SOURCE_BYTES + 1 })).toMatch(/too large/i);
  });

  it("applies the upload cap to the result only", () => {
    expect(resultProblem({ size: 52_000_000 })).toBeNull();
    expect(resultProblem({ size: 600_000_000 })).toMatch(/too large/i);
    expect(resultProblem({ size: 50 }, 10)).toMatch(/too large/i);
  });

  it("offers both audio and video in the file picker", () => {
    expect(MEDIA_ACCEPT_ATTR).toContain(".wav");
    expect(MEDIA_ACCEPT_ATTR).toContain(".mp4");
    expect(MEDIA_ACCEPT_ATTR).toContain(".mkv");
    expect(MEDIA_ACCEPT_ATTR).toContain("audio/*");
    expect(MEDIA_ACCEPT_ATTR).toContain("video/*");
  });
});
