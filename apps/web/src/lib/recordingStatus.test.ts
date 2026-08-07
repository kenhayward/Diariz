import { describe, it, expect } from "vitest";
import { hasTranscript, isProcessing, showStatusBadge, statusBadgeClass, statusLabel } from "./recordingStatus";
import type { RecordingStatus } from "./types";

/// Every status the API can send. Kept here so the badge-class test can assert exhaustiveness: the enum is
/// append-only on the server, and a status with no colour would render an empty class.
const ALL: RecordingStatus[] = [
  "Uploaded", "Queued", "Transcribing", "Transcribed", "Summarizing", "Summarized", "Merging", "Failed",
];

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

describe("hasTranscript", () => {
  it("is true once a transcript exists (including while it is being summarised)", () => {
    for (const s of ["Transcribed", "Summarizing", "Summarized"] as RecordingStatus[])
      expect(hasTranscript(s)).toBe(true);
  });

  it("is false before transcription finishes, and for a failed job", () => {
    for (const s of ["Uploaded", "Queued", "Transcribing", "Merging", "Failed"] as RecordingStatus[])
      expect(hasTranscript(s)).toBe(false);
  });
});

describe("showStatusBadge", () => {
  it("hides the pill for the settled success states, which would repeat on every row", () => {
    expect(showStatusBadge("Transcribed")).toBe(false);
    expect(showStatusBadge("Summarized")).toBe(false);
  });

  it("shows the pill while the pipeline is moving, and on failure", () => {
    for (const s of ["Uploaded", "Queued", "Transcribing", "Summarizing", "Merging", "Failed"] as RecordingStatus[])
      expect(showStatusBadge(s)).toBe(true);
  });
});

describe("statusBadgeClass", () => {
  it("gives every status a non-empty class", () => {
    for (const s of ALL) expect(statusBadgeClass(s)).not.toBe("");
  });

  it("colours the in-flight states amber, the settled ones green, and a failure red", () => {
    expect(statusBadgeClass("Transcribing")).toContain("amber");
    expect(statusBadgeClass("Summarizing")).toContain("amber");
    expect(statusBadgeClass("Transcribed")).toContain("green");
    expect(statusBadgeClass("Summarized")).toContain("green");
    expect(statusBadgeClass("Failed")).toContain("red");
  });

  it("carries a dark-mode variant for every status", () => {
    for (const s of ALL) expect(statusBadgeClass(s)).toContain("dark:");
  });

  it("falls back to a neutral pill for a status this build does not know", () => {
    // RecordingStatus is append-only on the server (ints in Postgres), so a deployed web build can be
    // handed a status added after it shipped. Without a fallback the row rendered class="... undefined",
    // which is unstyled text where a pill should be, on the one row the user most needs to read.
    const cls = statusBadgeClass("Archived" as RecordingStatus);
    expect(cls).not.toBe("");
    expect(cls).not.toContain("undefined");
    expect(cls).toContain("dark:"); // legible in both themes, like every known status
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
