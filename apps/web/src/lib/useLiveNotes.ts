import { useRef, useState } from "react";
import { savePendingNotes, clearPendingNotes } from "./pendingNotes";
import type { MeetingNote } from "./types";

export interface LiveNotes {
  /// The lines, for rendering. Use `snapshot()` anywhere state may not have flushed yet.
  lines: MeetingNote[];
  /// The lines as they are *now*. `upload()` reads them after its first await, when React state can
  /// still be a render behind, so a rendered value would be wrong there.
  snapshot(): MeetingNote[];
  /// Commit a line, stamped from the caller's recorded clock.
  add(text: string): void;
  /// Change a line's text. The stamp is immutable - it records when the thought was had, not when it
  /// was last tidied up.
  edit(id: string, text: string): void;
  remove(id: string): void;
  /// Drop every line, in memory and in the durable stash. Awaited by callers that need the stash gone
  /// before they continue (discarding audio); fire-and-forget is fine at the start of a recording.
  reset(): Promise<void>;
}

/**
 * The note lines a user types while recording: the in-memory list, and its mirror in IndexedDB so a
 * crash or a session lapse never loses them.
 *
 * Extracted from `Recorder.tsx` so this can be exercised without mounting a recorder, a media stream
 * and an upload path. Deliberately does NOT own attach-on-stop or the retry banner - those reach into
 * the upload flow and the API, and belong with the component that owns them.
 *
 * The hook never reads a clock. `stampMs` comes from the caller because the *recorded* clock is
 * pause-aware and lives in the recorder; a second clock in here would drift the first time a user
 * paused. The pop-out notes window depends on that being true - it sends text and nothing else.
 */
export function useLiveNotes({
  userId,
  stampMs,
}: {
  /// Null when nobody is signed in. Everything degrades to memory-only rather than branching at the
  /// call sites - the stash is keyed by user, so there is nowhere to put lines without one.
  userId: string | null;
  /// Offset into the recording, in ms, at the moment of the call.
  stampMs: () => number;
}): LiveNotes {
  const [lines, setLines] = useState<MeetingNote[]>([]);
  const linesRef = useRef<MeetingNote[]>([]);

  /// Update the lines and mirror them to IndexedDB (recordingId null = still recording).
  function mirror(next: MeetingNote[]) {
    linesRef.current = next;
    setLines(next);
    if (userId)
      void savePendingNotes({
        userId,
        recordingId: null,
        updatedAt: Date.now(),
        lines: next.map((l) => ({ text: l.text, capturedAtMs: l.capturedAtMs })),
      });
  }

  return {
    lines,
    snapshot: () => linesRef.current,

    add(text: string) {
      const line: MeetingNote = {
        id: crypto.randomUUID(),
        text,
        capturedAtMs: stampMs(),
        ordinal: linesRef.current.length,
        createdAt: new Date().toISOString(),
      };
      mirror([...linesRef.current, line]);
    },

    edit(id: string, text: string) {
      mirror(linesRef.current.map((l) => (l.id === id ? { ...l, text } : l)));
    },

    remove(id: string) {
      mirror(linesRef.current.filter((l) => l.id !== id));
    },

    async reset() {
      linesRef.current = [];
      setLines([]);
      if (userId) await clearPendingNotes(userId);
    },
  };
}
