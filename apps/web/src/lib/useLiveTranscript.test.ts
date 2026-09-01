import { describe, expect, it, vi } from "vitest";
import { nextLiveState, type LiveState } from "./useLiveTranscript";
import { emptyLiveTranscript } from "./liveTranscript";

const RECORDING = "rec-1";

function fresh(): LiveState {
  return { transcript: emptyLiveTranscript(RECORDING), degraded: false };
}

const seg = (startMs: number, text: string, sequence: number) => ({
  id: `${sequence}-${startMs}`,
  startMs,
  endMs: startMs + 3000,
  text,
  sequence,
});

describe("nextLiveState", () => {
  it("adds the fetched segments for the chunk that was announced", () => {
    const s = nextLiveState(fresh(), {
      kind: "append",
      recordingId: RECORDING,
      sequence: 0,
      segments: [seg(0, "shall we make a start", 0)],
    });

    expect(s.transcript.segments.map((x) => x.text)).toEqual(["shall we make a start"]);
  });

  it("clears the degraded flag once text starts arriving again", () => {
    // Degradation is not permanent: the server resumes once it has caught up, and a status line stuck
    // on "paused" while text visibly appears would be worse than not having one.
    const s = nextLiveState({ ...fresh(), degraded: true }, {
      kind: "append",
      recordingId: RECORDING,
      sequence: 0,
      segments: [seg(0, "back again", 0)],
    });

    expect(s.degraded).toBe(false);
  });

  it("marks the transcript degraded without discarding what has already arrived", () => {
    // The text so far is still true and still useful. Clearing it would punish the reader for a
    // server-side hiccup.
    let s = nextLiveState(fresh(), {
      kind: "append",
      recordingId: RECORDING,
      sequence: 0,
      segments: [seg(0, "before the gap", 0)],
    });
    s = nextLiveState(s, { kind: "degraded", recordingId: RECORDING, sequence: 1 });

    expect(s.degraded).toBe(true);
    expect(s.transcript.segments.map((x) => x.text)).toEqual(["before the gap"]);
  });

  it("ignores events for another recording entirely", () => {
    const s = nextLiveState(fresh(), { kind: "degraded", recordingId: "rec-2", sequence: 1 });
    expect(s.degraded).toBe(false);
  });

  it("returns the same state object for another recording's event, so React does not re-render", () => {
    // The hub is per user, so a page with a recording open receives events for every other one too.
    const before = fresh();
    const after = nextLiveState(before, { kind: "degraded", recordingId: "rec-2", sequence: 1 });
    expect(after).toBe(before);
  });

  it("returns the same state object when an event changes nothing", () => {
    // The case above stops at the recordingId guard and never reaches the branch below it, so it
    // passes just as well against a version that allocates on every event. This one is for OUR
    // recording, already degraded: repeated failure notices arrive chunk after chunk while the
    // transcriber is behind, and each one re-rendering the panel would be a needless churn.
    const before: LiveState = { ...fresh(), degraded: true };
    const after = nextLiveState(before, { kind: "degraded", recordingId: RECORDING, sequence: 7 });
    expect(after).toBe(before);
  });

  it("keeps a retroactive relabel, because the refetch returns the whole transcript", () => {
    // A merge on the server can rename a speaker on text the user is already looking at. The append is
    // only a signal to refetch, and the refetch returns everything - so an earlier line silently adopts
    // the corrected name. This is why the model replaces rather than appends: an append-only client
    // would keep showing a split the server has abandoned.
    let s = nextLiveState(fresh(), {
      kind: "append",
      recordingId: RECORDING,
      sequence: 0,
      segments: [{ ...seg(0, "before the merge", 0), speaker: "SPEAKER_01" }],
    });

    s = nextLiveState(s, {
      kind: "append",
      recordingId: RECORDING,
      sequence: 0,
      segments: [{ ...seg(0, "before the merge", 0), speaker: "Ada" }],
    });

    expect(s.transcript.segments.map((x) => x.speaker)).toEqual(["Ada"]);
  });
});
