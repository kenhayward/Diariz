import { describe, expect, it, vi } from "vitest";
import { startLiveSession, type LiveSessionDeps } from "./liveSession";
import { DEFAULT_CHUNKER_LIMITS, type ChunkerLimits } from "./liveChunker";
import type { ChunkStore, StoredChunk } from "./liveChunkQueue";

function memoryStore(): ChunkStore {
  const rows = new Map<number, StoredChunk>();
  return {
    async put(c) { rows.set(c.sequence, c); },
    async remove(s) { rows.delete(s); },
    async all() { return [...rows.values()].sort((a, b) => a.sequence - b.sequence); },
  };
}

const blob = (body = "audio") => new Blob([body], { type: "audio/webm" });

function deps(over: Partial<LiveSessionDeps> = {}): LiveSessionDeps {
  return {
    begin: async () => ({ id: "rec-1", sessionId: "sess-1" }),
    upload: async () => {},
    finalize: async () => {},
    requestFragment: () => {},
    store: memoryStore(),
    ...over,
  };
}

/// Drive enough loud ticks to pass the minimum, then a pause long enough to be a boundary.
function toBoundary(s: { tick: (d: number, l: number, p: boolean) => void }, limits: ChunkerLimits = DEFAULT_CHUNKER_LIMITS) {
  for (let t = 0; t < limits.minMs + 1000; t += 100) s.tick(100, 0.4, false);
  for (let t = 0; t < limits.pauseMs + 200; t += 100) s.tick(100, 0.01, false);
}

describe("startLiveSession", () => {
  it("chunks on the limits the server sent with the session", async () => {
    // The whole point of sending them: a deployment can shorten chunks - the dominant term in live
    // latency - and every client picks it up on its next recording, with no web deploy.
    const server = { minMs: 2_000, maxMs: 4_000, pauseMs: 300 };
    const requestFragment = vi.fn();
    const s = (await startLiveSession(deps({
      begin: async () => ({ id: "rec-1", sessionId: "sess-1", chunkLimits: server }),
      requestFragment,
    })))!;

    // Past the server's minimum but well short of the built-in one, then its shorter pause.
    for (let t = 0; t < server.minMs + 500; t += 100) s.tick(100, 0.4, false);
    for (let t = 0; t < server.pauseMs + 200; t += 100) s.tick(100, 0.01, false);

    expect(requestFragment).toHaveBeenCalledTimes(1);
  });

  it("ignores limits that cannot work and keeps recording", async () => {
    // A maximum at or below the minimum would cut on every tick - a chunk uploaded per animation frame.
    // The values come from a deployment file somebody can typo, so a bad one costs the retune, not the
    // meeting.
    const requestFragment = vi.fn();
    const s = (await startLiveSession(deps({
      begin: async () => ({ id: "rec-1", sessionId: "sess-1", chunkLimits: { minMs: 9_000, maxMs: 1_000, pauseMs: 0 } }),
      requestFragment,
    })))!;

    s.tick(100, 0.4, false);
    expect(requestFragment).not.toHaveBeenCalled();

    toBoundary(s);
    expect(requestFragment).toHaveBeenCalledTimes(1);
  });

  it("returns null when the server cannot be reached, so the recorder falls back", async () => {
    // The single most important behaviour in this module. A briefly unreachable server must cost
    // the live transcript, never the meeting - the caller buffers locally and uploads at stop.
    const session = await startLiveSession(deps({
      begin: async () => { throw new Error("network"); },
    }));

    expect(session).toBeNull();
  });

  it("asks for a fragment when a chunk boundary is reached", async () => {
    const requestFragment = vi.fn();
    const session = (await startLiveSession(deps({ requestFragment })))!;

    toBoundary(session);

    expect(requestFragment).toHaveBeenCalledTimes(1);
  });

  it("does not ask again while a fragment is still outstanding", async () => {
    // Two requests before the first blob arrives would produce two chunks covering one span, and the
    // server's contiguity check would then see overlapping times.
    const requestFragment = vi.fn();
    const session = (await startLiveSession(deps({ requestFragment })))!;

    toBoundary(session);
    toBoundary(session);

    expect(requestFragment).toHaveBeenCalledTimes(1);
  });

  it("resumes cutting once the fragment has arrived", async () => {
    const requestFragment = vi.fn();
    const session = (await startLiveSession(deps({ requestFragment })))!;

    toBoundary(session);
    await session.offerFragment(blob(), 21_000);
    toBoundary(session);

    expect(requestFragment).toHaveBeenCalledTimes(2);
  });

  it("uploads each fragment with the span it covers, in order", async () => {
    const sent: { sequence: number; startMs: number; endMs: number }[] = [];
    const session = (await startLiveSession(deps({
      upload: async (_id, _s, c) => void sent.push({ sequence: c.sequence, startMs: c.startMs, endMs: c.endMs }),
    })))!;

    await session.offerFragment(blob("a"), 21_000);
    await session.offerFragment(blob("b"), 43_000);

    expect(sent).toEqual([
      { sequence: 0, startMs: 0, endMs: 21_000 },
      { sequence: 1, startMs: 21_000, endMs: 43_000 },
    ]);
  });

  it("carries the recording and session ids on every chunk", async () => {
    // The session id is what stops a second device interleaving its audio, so it has to be on the
    // chunk and not merely on the begin call.
    const seen: string[] = [];
    const session = (await startLiveSession(deps({
      upload: async (id, sessionId) => void seen.push(`${id}/${sessionId}`),
    })))!;

    await session.offerFragment(blob(), 21_000);

    expect(seen).toEqual(["rec-1/sess-1"]);
  });

  it("ignores an empty fragment", async () => {
    const upload = vi.fn();
    const session = (await startLiveSession(deps({ upload })))!;

    await session.offerFragment(new Blob([], { type: "audio/webm" }), 21_000);

    expect(upload).not.toHaveBeenCalled();
  });

  it("queues the tail and finalises at stop", async () => {
    const sent: number[] = [];
    const finalize = vi.fn().mockResolvedValue(undefined);
    const session = (await startLiveSession(deps({
      upload: async (_i, _s, c) => void sent.push(c.sequence),
      finalize,
    })))!;

    await session.offerFragment(blob("a"), 21_000);
    await session.finish(30_000, blob("tail"));

    expect(sent).toEqual([0, 1]);
    expect(finalize).toHaveBeenCalledWith("rec-1");
  });

  it("still finalises when there is no tail", async () => {
    const finalize = vi.fn().mockResolvedValue(undefined);
    const session = (await startLiveSession(deps({ finalize })))!;

    await session.finish(0);

    expect(finalize).toHaveBeenCalledWith("rec-1");
  });

  it("reports trouble rather than throwing when a chunk will not upload", async () => {
    // Recording must continue through a network problem; the user is told the audio is not safe yet,
    // and the queue keeps it for the next drain.
    const onTrouble = vi.fn();
    const session = (await startLiveSession(deps({
      upload: async () => { throw new Error("network"); },
      onTrouble,
    })))!;

    await session.offerFragment(blob(), 21_000);

    expect(await session.pending()).toEqual([0]);
  });

  it("keeps unsent chunks visible after a failed finish", async () => {
    const session = (await startLiveSession(deps({
      upload: async () => { throw new Error("network"); },
      finalize: async () => {},
    })))!;

    await session.offerFragment(blob(), 21_000);
    await session.finish(21_000);

    expect(await session.pending()).toEqual([0]);
  });
});
