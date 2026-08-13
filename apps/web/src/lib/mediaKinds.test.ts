import { describe, it, expect } from "vitest";
import {
  classifyFile,
  sourceProblem,
  resultProblem,
  uploadableWithoutExtraction,
  MEDIA_ACCEPT_ATTR,
  MAX_SOURCE_BYTES,
} from "./mediaKinds";

describe("mediaKinds", () => {
  it("classifies audio formats as pass-through", () => {
    for (const name of ["a.wav", "a.mp3", "a.flac", "a.ogg", "a.oga", "a.opus", "a.m4a", "a.m4b", "a.aac"])
      expect(classifyFile({ name }), name).toBe("audio");
  });

  it("classifies containers that may hold video, including .webm", () => {
    for (const name of ["a.mp4", "a.m4v", "a.mov", "a.mkv", "a.webm", "a.ts", "a.m2ts", "a.mts", "a.3gp", "a.3g2"])
      expect(classifyFile({ name }), name).toBe("container");
  });

  it("rejects formats we cannot demux", () => {
    for (const name of ["a.avi", "a.wmv", "a.flv", "a.txt", "noext"])
      expect(classifyFile({ name }), name).toBe("rejected");
  });

  // The extractor (mediabunny, ALL_FORMATS) detects by magic bytes and ships Ogg, QuickTime, Matroska,
  // WebM, Wave, Flac, Adts and MpegTs readers. These extensions were demuxable all along but the
  // extension gate refused them, and because `accept` also carries audio/*,video/* the file dialog
  // offered them first - so the user picked a file the app then called unsupported.
  it("accepts the formats the extractor can already demux", () => {
    for (const name of ["talk.oga", "book.m4b", "cam.ts", "cam.m2ts", "cam.mts", "phone.3gp", "phone.3g2"])
      expect(classifyFile({ name }), name).not.toBe("rejected");
  });

  it("offers every classified extension in the file dialog", () => {
    // The dialog and the validator must agree: anything offered must classify, or the user picks a file
    // and is then told it is unsupported.
    for (const ext of ["oga", "m4b", "ts", "m2ts", "mts", "3gp", "3g2"])
      expect(MEDIA_ACCEPT_ATTR, ext).toContain(`.${ext}`);
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

  it("lets only .webm upload unextracted, since it was accepted before this feature", () => {
    expect(uploadableWithoutExtraction({ name: "clip.webm" })).toBe(true);
    for (const name of ["a.mp4", "a.m4v", "a.mov", "a.mkv"])
      expect(uploadableWithoutExtraction({ name }), name).toBe(false);
  });

  it("offers both audio and video in the file picker", () => {
    expect(MEDIA_ACCEPT_ATTR).toContain(".wav");
    expect(MEDIA_ACCEPT_ATTR).toContain(".mp4");
    expect(MEDIA_ACCEPT_ATTR).toContain(".mkv");
    expect(MEDIA_ACCEPT_ATTR).toContain("audio/*");
    expect(MEDIA_ACCEPT_ATTR).toContain("video/*");
  });
});
