/// Durable stash for screenshots captured during a recording, so a crash or a session lapse never loses
/// them. Mirrors pendingNotes conceptually (its own databases - adding a store to an existing DB would
/// force a version bump across modules) but NOT its single-record shape: each capture carries a
/// full-resolution PNG Blob plus a JPEG thumbnail, so rewriting the *whole* growing set on every capture
/// (as a single-record stash would require) makes N captures cost O(N^2) cumulative IndexedDB writes - a
/// representative ~3 MB/capture puts 200 captures at ~60 GB of churn. Instead this is a per-item stash
/// (`createMultiKeyedStash`): adding a capture writes exactly that one record, and a small separate
/// single-record stash tracks the (rarely-written) `recordingId` meta. All operations degrade to no-ops
/// without IndexedDB (see keyedStash.ts, which owns the actual IndexedDB plumbing shared with
/// pendingNotes).
///
/// This stash has never shipped (no released version wrote the old whole-record shape to a real user's
/// browser), so there is nothing to migrate - see the PR description for how that was confirmed.

import { createKeyedStash, createMultiKeyedStash } from "./keyedStash";

export interface PendingShot {
  /// Stable per-capture id, assigned when the capture is stashed. Used to delete exactly this capture
  /// (live-strip delete button) and to mark exactly this capture uploaded during attach.
  id: string;
  capturedAtMs: number;
  width: number;
  height: number;
  full: Blob;
  thumb: Blob;
}

/// A capture as stored in the item store: the same fields as `PendingShot`, plus the user id used to
/// look it up (the index field) and to scope deletes.
interface StoredShot extends PendingShot {
  userId: string;
}

interface Meta {
  userId: string;
  /// Null while recording; the created recording's id once audio uploaded but the attach failed.
  recordingId: string | null;
  updatedAt: number;
}

/// The composed view callers work with: every capture currently stashed for a user, plus where (if
/// anywhere) they should be attached. Mirrors the pre-refactor `PendingScreenshots` shape so callers
/// (Recorder's mount-time recovery check, the retry banner) don't need to know about the meta/item split.
export interface PendingScreenshots {
  userId: string;
  shots: PendingShot[];
  recordingId: string | null;
  updatedAt: number;
}

const metaStash = createKeyedStash<Meta>("diariz-screenshots", "pending-screenshots-meta", "userId");
const itemsStash = createMultiKeyedStash<StoredShot>("diariz-screenshot-items", "items", "id", "userId");

/// Stash exactly one new capture - a single record write, not a rewrite of the whole growing set. This is
/// the operation the capture hot path (Recorder.addLiveShot) must call once per capture. The caller
/// assigns `shot.id` (Recorder uses `crypto.randomUUID()`, same as it already does for note lines) so it
/// can update its in-memory ref/state synchronously without waiting on this write to resolve.
export async function addPendingScreenshot(userId: string, shot: PendingShot): Promise<void> {
  await itemsStash.add({ userId, ...shot });
}

/// Every capture currently stashed for a user (in capture order), plus the recordingId meta. Null when
/// nothing at all is stashed (no captures and no meta), matching the pre-refactor "nothing stashed" case.
export async function loadPendingScreenshots(userId: string): Promise<PendingScreenshots | null> {
  const stored = await itemsStash.list(userId);
  const meta = await metaStash.load(userId);
  if (stored.length === 0 && !meta) return null;
  const shots = stored
    .slice()
    .sort((a, b) => a.capturedAtMs - b.capturedAtMs)
    .map(({ userId: _userId, ...shot }) => shot);
  return {
    userId,
    shots,
    recordingId: meta?.recordingId ?? null,
    updatedAt: meta?.updatedAt ?? 0,
  };
}

/// Remove exactly one capture by id - the live-strip delete button, or marking one capture uploaded
/// during attach - without touching any other stashed capture.
export async function removePendingScreenshot(userId: string, id: string): Promise<void> {
  void userId; // primary key (id) already uniquely identifies the record; kept for a symmetric call shape
  await itemsStash.remove(id);
}

/// Update just the recordingId meta (set once the audio has uploaded but the attach failed, so a retry
/// knows where the still-stashed captures belong) without touching any capture.
export async function setPendingScreenshotsRecordingId(
  userId: string,
  recordingId: string | null,
): Promise<void> {
  await metaStash.save({ userId, recordingId, updatedAt: Date.now() });
}

/// Wipe every capture and the recordingId meta for a user (a fresh recording starting, or a fully
/// successful attach).
export async function clearPendingScreenshots(userId: string): Promise<void> {
  await itemsStash.clear(userId);
  await metaStash.clear(userId);
}
