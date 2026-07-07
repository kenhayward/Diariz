import type { RecordingStatus } from "./types";

/// Tone of a status-bar message, driving its colour. Mirrors the existing banner palette:
/// progress = amber (in-flight), success = green, error = red, info = neutral grey.
export type StatusTone = "progress" | "success" | "error" | "info";

/// The representative in-flight pipeline message for the whole account, derived from the recordings list
/// (so the bar reflects background work on any recording, not just the open one). Priority reflects how far
/// along the pipeline a recording is: Merging → Transcribing → Summarizing. The pre-transcript wait
/// (Queued/Uploaded) is reported as "transcribing" too, so the user sees transcription in progress rather
/// than an internal queue state. Returns the i18n key (workspace namespace) + tone, or null when idle.
export function pipelineStatus(
  recordings: { status: RecordingStatus }[],
): { key: string; tone: StatusTone } | null {
  const has = (s: RecordingStatus) => recordings.some((r) => r.status === s);
  if (has("Merging")) return { key: "merging", tone: "progress" };
  if (has("Transcribing") || has("Queued") || has("Uploaded")) return { key: "transcribing", tone: "progress" };
  if (has("Summarizing")) return { key: "summarising", tone: "progress" };
  return null;
}

/// Text colour classes for a tone (light + dark), matching the recording-page banners.
export function toneClass(tone: StatusTone): string {
  switch (tone) {
    case "progress":
      return "text-amber-700 dark:text-amber-300";
    case "success":
      return "text-green-700 dark:text-green-300";
    case "error":
      return "text-red-700 dark:text-red-300";
    default:
      return "text-gray-500 dark:text-gray-400";
  }
}
