/// Durable stash for live-recording note lines, so a crash/session lapse never loses them. Mirrors the
/// pendingRecording pattern but in its own database (`diariz-notes`) - adding a store to the existing
/// `diariz` DB would force a version bump across modules. Keyed by user id. `recordingId` is null while
/// the recording is still in progress; it is set when the audio uploaded but the notes attach failed, so
/// the retry banner knows where the lines belong. All operations degrade to no-ops without IndexedDB
/// (see keyedStash.ts, which owns the actual IndexedDB plumbing shared with pendingScreenshots).

import { createKeyedStash } from "./keyedStash";

export interface PendingNoteLine {
  text: string;
  capturedAtMs: number | null;
}

export interface PendingNotes {
  userId: string;
  lines: PendingNoteLine[];
  /// Null while recording; the created recording's id once audio uploaded but the notes attach failed.
  recordingId: string | null;
  updatedAt: number;
}

// Database/store names are unchanged from before the keyedStash extraction - existing stashes already
// sitting in a user's browser from a prior session must keep resolving, so neither may ever change.
const stash = createKeyedStash<PendingNotes>("diariz-notes", "pending-notes", "userId");

export async function savePendingNotes(notes: PendingNotes): Promise<void> {
  await stash.save(notes);
}

export async function loadPendingNotes(userId: string): Promise<PendingNotes | null> {
  return stash.load(userId);
}

export async function clearPendingNotes(userId: string): Promise<void> {
  await stash.clear(userId);
}
