import { describe, it, expect } from "vitest";
import { pipelineStatus, toneClass, type StatusTone } from "./statusBar";
import type { RecordingStatus } from "./types";

const recs = (...statuses: RecordingStatus[]) => statuses.map((status) => ({ status }));

describe("pipelineStatus", () => {
  it("returns null when nothing is in flight", () => {
    expect(pipelineStatus(recs("Transcribed", "Summarized", "Failed"))).toBeNull();
    expect(pipelineStatus([])).toBeNull();
  });

  it("prioritises Merging over everything else", () => {
    expect(pipelineStatus(recs("Summarizing", "Transcribing", "Merging"))).toEqual({
      key: "merging",
      tone: "progress",
    });
  });

  it("shows the pre-transcript wait (Queued/Uploaded) as Transcribing, then Summarizing", () => {
    expect(pipelineStatus(recs("Summarizing", "Transcribing"))?.key).toBe("transcribing");
    // Queued/Uploaded read as "transcribing" too - the user shouldn't see an internal queue state.
    expect(pipelineStatus(recs("Summarizing", "Queued"))?.key).toBe("transcribing");
    expect(pipelineStatus(recs("Summarizing", "Uploaded"))?.key).toBe("transcribing");
    expect(pipelineStatus(recs("Summarizing"))?.key).toBe("summarising");
  });

  it("marks in-flight statuses with the progress tone", () => {
    expect(pipelineStatus(recs("Transcribing"))?.tone).toBe("progress");
  });
});

describe("toneClass", () => {
  it("maps each tone to a colour class", () => {
    const tones: StatusTone[] = ["progress", "success", "error", "info"];
    for (const tone of tones) expect(toneClass(tone)).toBeTruthy();
    expect(toneClass("progress")).toContain("amber");
    expect(toneClass("success")).toContain("green");
    expect(toneClass("error")).toContain("red");
    expect(toneClass("info")).toContain("gray");
  });
});
