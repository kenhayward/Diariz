import { useRef, useState } from "react";
import { savePendingNotes, clearPendingNotes } from "./pendingNotes";
import type { LineKind, LiveNoteLine } from "./types";

export interface LiveNotes {
  /// The lines, for rendering. Use `snapshot()` anywhere state may not have flushed yet.
  lines: LiveNoteLine[];
  /// The lines as they are *now*. `upload()` reads them after its first await, when React state can
  /// still be a render behind, so a rendered value would be wrong there.
  snapshot(): LiveNoteLine[];
  /// Commit a line. Stamped from the caller's recorded clock, unless `atMs` names the moment instead -
  /// which is how a note gets pinned to something said earlier in the meeting. `kind` defaults to
  /// "note"; an "action" line is filed to the default actor immediately, same as `setKind` does.
  add(text: string, atMs?: number, kind?: LineKind): void;
  /// Change a line's text. The stamp is immutable - it records when the thought was had, not when it
  /// was last tidied up.
  edit(id: string, text: string): void;
  remove(id: string): void;
  /// Switch a line between note and action. Promoting to "action" fills the owner with `defaultActor()`
  /// only if the line has none yet - an owner someone typed survives a round trip through "note".
  setKind(id: string, kind: LineKind): void;
  /// Patch an action's owner and/or due date. No-op fields are omitted, not cleared.
  updateAction(id: string, patch: { actor?: string; deadline?: string }): void;
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
  defaultActor,
}: {
  /// Null when nobody is signed in. Everything degrades to memory-only rather than branching at the
  /// call sites - the stash is keyed by user, so there is nowhere to put lines without one.
  userId: string | null;
  /// Offset into the recording, in ms, at the moment of the call.
  stampMs: () => number;
  /// Who a new action belongs to until someone says otherwise: the signed-in user. A function, like
  /// stampMs, so the hook never caches a name across a sign-in.
  defaultActor: () => string;
}): LiveNotes {
  const [lines, setLines] = useState<LiveNoteLine[]>([]);
  const linesRef = useRef<LiveNoteLine[]>([]);

  /// Update the lines and mirror them to IndexedDB (recordingId null = still recording).
  function mirror(next: LiveNoteLine[]) {
    linesRef.current = next;
    setLines(next);
    if (userId)
      void savePendingNotes({
        userId,
        recordingId: null,
        updatedAt: Date.now(),
        lines: next.map((l) => ({
          text: l.text,
          capturedAtMs: l.capturedAtMs,
          kind: l.kind,
          actor: l.actor,
          deadline: l.deadline,
        })),
      });
  }

  return {
    lines,
    snapshot: () => linesRef.current,

    add(text: string, atMs?: number, kind: LineKind = "note") {
      const line: LiveNoteLine = {
        id: crypto.randomUUID(),
        text,
        // `ordinal` still counts from the list length rather than from the stamp: it records the order
        // the lines were WRITTEN, which is what the server sorts them by on attach. Pinning a note to
        // an earlier moment moves where it reads, not when it was thought of.
        capturedAtMs: atMs ?? stampMs(),
        ordinal: linesRef.current.length,
        createdAt: new Date().toISOString(),
        ...(kind === "action" ? { kind, actor: defaultActor() } : {}),
      };
      mirror([...linesRef.current, line]);
    },

    edit(id: string, text: string) {
      mirror(linesRef.current.map((l) => (l.id === id ? { ...l, text } : l)));
    },

    remove(id: string) {
      mirror(linesRef.current.filter((l) => l.id !== id));
    },

    setKind(id: string, kind: LineKind) {
      mirror(
        linesRef.current.map((l) =>
          l.id !== id
            ? l
            : // An owner someone typed survives a round trip through "note"; only a line that never had one
              // is given the default.
              kind === "action"
              ? { ...l, kind, actor: l.actor ?? defaultActor() }
              : { ...l, kind },
        ),
      );
    },

    updateAction(id: string, patch: { actor?: string; deadline?: string }) {
      const clean = Object.fromEntries(Object.entries(patch).filter(([, v]) => v !== undefined));
      mirror(linesRef.current.map((l) => (l.id === id ? { ...l, ...clean } : l)));
    },

    async reset() {
      linesRef.current = [];
      setLines([]);
      if (userId) await clearPendingNotes(userId);
    },
  };
}
