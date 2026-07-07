import { describe, it, expect } from "vitest";
import { isProcessing, statusLabel } from "./recordingStatus";
import type { RecordingStatus } from "./types";

describe("isProcessing", () => {
  it("is true while the transcript pipeline is still running", () => {
    for (const s of ["Uploaded", "Queued", "Transcribing", "Merging"] as RecordingStatus[])
      expect(isProcessing(s)).toBe(true);
  });

  it("is false once a transcript exists or the job failed", () => {
    for (const s of ["Transcribed", "Summarizing", "Summarized", "Failed"] as RecordingStatus[])
      expect(isProcessing(s)).toBe(false);
  });
});

describe("statusLabel", () => {
  it("shows the pre-transcript wait (Uploaded/Queued) as Transcribing", () => {
    expect(statusLabel("Uploaded")).toBe("Transcribing");
    expect(statusLabel("Queued")).toBe("Transcribing");
  });

  it("passes other statuses through unchanged", () => {
    for (const s of ["Transcribing", "Merging", "Summarizing", "Failed"] as RecordingStatus[])
      expect(statusLabel(s)).toBe(s);
  });
});
