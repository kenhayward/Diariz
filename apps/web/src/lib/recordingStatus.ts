import type { RecordingStatus } from "./types";

/// Whether the transcription pipeline is still running (no transcript exists yet). Actions that assume a
/// finished transcript - notably Delete - are disabled while a recording is in one of these states. A Failed
/// recording is NOT processing (it can be deleted).
export function isProcessing(status: RecordingStatus): boolean {
  return status === "Uploaded" || status === "Queued" || status === "Transcribing" || status === "Merging";
}

/// Whether a transcript exists to act on. Summarizing/Summarized still have one - the summary is generated
/// from it - so menu items that read the transcript stay enabled throughout.
export function hasTranscript(status: RecordingStatus): boolean {
  return status === "Transcribed" || status === "Summarizing" || status === "Summarized";
}

/// Show the status pill only while the pipeline is moving. The settled success states
/// (Transcribed/Summarized) repeat on every row and truncate the name, so they're hidden.
export function showStatusBadge(status: RecordingStatus): boolean {
  return status !== "Transcribed" && status !== "Summarized";
}

/// The status text shown on a recording's badge. The pre-transcript wait (Uploaded/Queued) reads as
/// "Transcribing" so the user sees transcription in progress rather than an internal queue state.
export function statusLabel(status: RecordingStatus): string {
  return status === "Uploaded" || status === "Queued" ? "Transcribing" : status;
}

/// Tailwind classes for a status pill: amber while the pipeline is moving, green when it has settled,
/// red on failure. A `Record` rather than a switch so a status added to the type without a colour is a
/// compile error here rather than a blank pill in the list.
const NEUTRAL_STATUS_CLASS = "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300";

const STATUS_CLASS: Record<RecordingStatus, string> = {
  Uploaded: NEUTRAL_STATUS_CLASS,
  Queued: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribed: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Summarizing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Summarized: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Merging: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Failed: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
};

/// The `Record` above keeps a status added to the *type* honest at compile time, but the enum is
/// append-only on the **server** - a deployed build can be handed a status that did not exist when it
/// shipped, which is a runtime miss the type system never sees. Fall back to the neutral pill: unfamiliar
/// but legible, rather than `class="... undefined"` on the row the user most needs to read.
export function statusBadgeClass(status: RecordingStatus): string {
  return STATUS_CLASS[status] ?? NEUTRAL_STATUS_CLASS;
}
