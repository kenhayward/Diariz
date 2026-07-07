import type { RecordingStatus } from "./types";

/// Whether the transcription pipeline is still running (no transcript exists yet). Actions that assume a
/// finished transcript - notably Delete - are disabled while a recording is in one of these states. A Failed
/// recording is NOT processing (it can be deleted).
export function isProcessing(status: RecordingStatus): boolean {
  return status === "Uploaded" || status === "Queued" || status === "Transcribing" || status === "Merging";
}

/// The status text shown on a recording's badge. The pre-transcript wait (Uploaded/Queued) reads as
/// "Transcribing" so the user sees transcription in progress rather than an internal queue state.
export function statusLabel(status: RecordingStatus): string {
  return status === "Uploaded" || status === "Queued" ? "Transcribing" : status;
}
