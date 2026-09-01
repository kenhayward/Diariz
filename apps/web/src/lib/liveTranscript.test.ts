import { describe, expect, it } from "vitest";
import {
  applyAppend,
  emptyLiveTranscript,
  lagSeconds,
  type LiveSegment,
  type LiveTranscript,
} from "./liveTranscript";

const seg = (startMs: number, text: string, sequence: number): LiveSegment => ({
  id: `${sequence}-${startMs}`,
  startMs,
  endMs: startMs + 3000,
  text,
  sequence,
});

const RECORDING = "rec-1";

function apply(state: LiveTranscript, sequence: number, segments: LiveSegment[], recordingId = RECORDING) {
  return applyAppend(state, { recordingId, sequence, segments });
}

describe("liveTranscript", () => {
  it("orders segments by recording time regardless of the order events arrive", () => {
    // Chunks complete out of order under retry, and the reader wants the meeting's order, not the
    // queue's.
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 2, [seg(60_000, "third", 2)]);
    s = apply(s, 0, [seg(0, "first", 0)]);
    s = apply(s, 1, [seg(30_000, "second", 1)]);

    expect(s.segments.map((x) => x.text)).toEqual(["first", "second", "third"]);
  });

  it("replaces a redelivered sequence rather than duplicating it", () => {
    // The client sees at-least-once too: the server can push the same append twice, and a naive
    // concat would show the sentence twice in the middle of the transcript.
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 0, [seg(0, "first attempt", 0)]);
    s = apply(s, 0, [seg(0, "redelivered", 0)]);

    expect(s.segments.map((x) => x.text)).toEqual(["redelivered"]);
  });

  it("lets a redelivery change how many segments a chunk has", () => {
    // A re-transcribe can split or merge lines, so replacing must be by chunk, not by segment id -
    // matching on ids would strand the ones that no longer exist.
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 0, [seg(0, "one line", 0)]);
    s = apply(s, 0, [seg(0, "split", 0), seg(1500, "in two", 0)]);

    expect(s.segments.map((x) => x.text)).toEqual(["split", "in two"]);
  });

  it("ignores an event for a different recording", () => {
    // Two meetings can be open at once - a running one and one being read - and the hub is per user,
    // not per recording.
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 0, [seg(0, "mine", 0)]);
    s = apply(s, 0, [seg(0, "somebody else's", 0)], "rec-2");

    expect(s.segments.map((x) => x.text)).toEqual(["mine"]);
  });

  it("does not truncate what is already shown when an older event arrives late", () => {
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 0, [seg(0, "first", 0)]);
    s = apply(s, 1, [seg(30_000, "second", 1)]);
    s = apply(s, 0, [seg(0, "first, corrected", 0)]);

    expect(s.segments.map((x) => x.text)).toEqual(["first, corrected", "second"]);
  });

  it("orders segments within one chunk by time too", () => {
    // Across chunks, sequence order and time order coincide, so the out-of-order test above passes
    // just as well against a sort by chunk. This is the case that separates them - and it is the
    // second time this exact blind spot has appeared, the server-side ordering having had it too.
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 0, [seg(6000, "later", 0), seg(0, "earlier", 0)]);

    expect(s.segments.map((x) => x.text)).toEqual(["earlier", "later"]);
  });

  it("tracks the highest sequence it has seen", () => {
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 0, [seg(0, "a", 0)]);
    s = apply(s, 4, [seg(120_000, "e", 4)]);
    s = apply(s, 2, [seg(60_000, "c", 2)]);

    expect(s.highestSequence).toBe(4);
  });

  it("keeps an empty chunk from disturbing the transcript", () => {
    // Silence mid-meeting is ordinary. The append still arrives, carrying no segments.
    let s = emptyLiveTranscript(RECORDING);
    s = apply(s, 0, [seg(0, "talking", 0)]);
    s = apply(s, 1, []);

    expect(s.segments.map((x) => x.text)).toEqual(["talking"]);
    expect(s.highestSequence).toBe(1);
  });
});

describe("lagSeconds", () => {
  it("is how far the newest text is behind the recorded clock", () => {
    // What the status line shows. The transcript covers up to 90 s; the meeting has run 100 s.
    expect(lagSeconds(90_000, 100_000)).toBe(10);
  });

  it("is zero when the transcript has caught up", () => {
    expect(lagSeconds(100_000, 100_000)).toBe(0);
  });

  it("never goes negative", () => {
    // Overlap means a chunk can carry a segment ending marginally past the clock read a moment
    // earlier. Reporting that as "-2s behind" would be nonsense on screen.
    expect(lagSeconds(102_000, 100_000)).toBe(0);
  });

  it("is zero before any transcript exists", () => {
    expect(lagSeconds(null, 100_000)).toBe(0);
  });
});
