import { describe, it, expect } from "vitest";
import { extractAudio } from "./videoAudio";

// jsdom has neither `Worker` nor `AudioDecoder`, which is precisely the "browser without WebCodecs"
// branch - so the fallback policy is directly testable here even though the conversion itself is not.

const file = (name: string) => new File(["x"], name, { type: "application/octet-stream" });
const opts = () => ({ onProgress: () => {}, signal: new AbortController().signal });

describe("extractAudio without WebCodecs", () => {
  it("still uploads a .webm, which worked before extraction existed", async () => {
    const f = file("clip.webm");
    await expect(extractAudio(f, opts())).resolves.toBe(f);
  });

  it("refuses a video rather than uploading it unchecked", async () => {
    await expect(extractAudio(file("Town Hall.mp4"), opts())).rejects.toThrow(/can't extract audio/i);
    await expect(extractAudio(file("clip.mov"), opts())).rejects.toThrow(/can't extract audio/i);
    await expect(extractAudio(file("clip.mkv"), opts())).rejects.toThrow(/can't extract audio/i);
  });
});
