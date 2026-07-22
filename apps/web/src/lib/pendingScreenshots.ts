/// Durable stash for screenshots captured during a recording, so a crash or a session lapse never loses
/// them. Mirrors pendingNotes, in its own database (`diariz-screenshots`) - adding a store to an existing
/// DB would force a version bump across modules. Keyed by user id. `recordingId` is null while the
/// recording is in progress; it is set when the audio uploaded but the screenshot attach failed, so the
/// retry path knows where the captures belong. Blobs are stored directly: IndexedDB handles them natively,
/// so no base64 inflation. All operations degrade to no-ops without IndexedDB (see keyedStash.ts, which
/// owns the actual IndexedDB plumbing shared with pendingNotes).

import { createKeyedStash } from "./keyedStash";

export interface PendingShot {
  capturedAtMs: number;
  width: number;
  height: number;
  full: Blob;
  thumb: Blob;
}

export interface PendingScreenshots {
  userId: string;
  shots: PendingShot[];
  /// Null while recording; the created recording's id once audio uploaded but the attach failed.
  recordingId: string | null;
  updatedAt: number;
}

const stash = createKeyedStash<PendingScreenshots>("diariz-screenshots", "pending-screenshots", "userId");

export async function savePendingScreenshots(value: PendingScreenshots): Promise<void> {
  await stash.save(value);
}

export async function loadPendingScreenshots(userId: string): Promise<PendingScreenshots | null> {
  return stash.load(userId);
}

export async function clearPendingScreenshots(userId: string): Promise<void> {
  await stash.clear(userId);
}
