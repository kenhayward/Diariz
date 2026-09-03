import { describe, expect, it } from "vitest";
import { buildStream, streamCounts, stampColumnPx } from "./notesStream";
import type { LiveSegment } from "./liveTranscript";
import type { MeetingNote, ShotView } from "./types";

const seg = (over: Partial<LiveSegment> & { startMs: number }): LiveSegment => ({
  id: `s-${over.startMs}`,
  endMs: over.startMs + 3000,
  text: "said something",
  sequence: 0,
  ...over,
});

const note = (over: Partial<MeetingNote> & { capturedAtMs: number | null }): MeetingNote => ({
  id: `n-${over.capturedAtMs}`,
  text: "a thought",
  ordinal: 0,
  createdAt: "2026-09-03T10:00:00.000Z",
  ...over,
});

const shot = (capturedAtMs: number): ShotView => ({
  id: `c-${capturedAtMs}`,
  capturedAtMs,
  thumb: new Blob(["t"], { type: "image/jpeg" }),
});

const build = (over: Partial<Parameters<typeof buildStream>[0]> = {}) =>
  buildStream({ lines: [], shots: [], segments: [], filter: "all", ...over });

describe("buildStream", () => {
  it("interleaves notes, captures and transcript lines on one timeline", () => {
    const items = build({
      lines: [note({ capturedAtMs: 20_000 })],
      shots: [shot(5_000)],
      segments: [seg({ startMs: 30_000 }), seg({ startMs: 10_000 })],
    });

    expect(items.map((i) => [i.kind, i.atMs])).toEqual([
      ["capture", 5_000],
      ["transcript", 10_000],
      ["note", 20_000],
      ["transcript", 30_000],
    ]);
  });

  it("reads a note after the line it was written about when both land on the same second", () => {
    // Someone hears a sentence and writes about it. Filing the note above the sentence would invert
    // cause and effect, which is the one ordering a reader would notice.
    const items = build({
      lines: [note({ capturedAtMs: 12_000 })],
      shots: [shot(12_000)],
      segments: [seg({ startMs: 12_000 })],
    });

    expect(items.map((i) => i.kind)).toEqual(["transcript", "note", "capture"]);
  });

  it("keeps two notes filed in the same second in the order they were written", () => {
    const items = build({
      lines: [
        note({ capturedAtMs: 9_000, id: "first" }),
        note({ capturedAtMs: 9_000, id: "second" }),
      ],
    });

    expect(items.map((i) => i.id)).toEqual(["n:first", "n:second"]);
  });

  it("floats a note with no stamp to the top rather than dropping it", () => {
    // A line adopted from a pre-meeting stash has no recorded moment. Sorting `null` as a number would
    // scatter it; hiding it would lose something the user typed.
    const items = build({
      lines: [note({ capturedAtMs: null, id: "adopted" })],
      segments: [seg({ startMs: 0 })],
    });

    expect(items[0].id).toBe("n:adopted");
    expect(items[0].atMs).toBe(0);
  });

  it("gives every row an id that cannot collide with another kind's", () => {
    // Server segment ids and browser-minted note/capture ids come from different sources; a React key
    // shared between two rows drops one of them silently.
    const items = build({
      lines: [note({ capturedAtMs: 1_000, id: "same" })],
      shots: [{ id: "same", capturedAtMs: 2_000, thumb: new Blob(["t"]) }],
      segments: [seg({ startMs: 3_000, id: "same" })],
    });

    expect(new Set(items.map((i) => i.id)).size).toBe(3);
  });

  it("carries the source row through, so the renderer needs nothing else", () => {
    const line = note({ capturedAtMs: 1_000 });
    const capture = shot(2_000);
    const segment = seg({ startMs: 3_000 });

    const items = build({ lines: [line], shots: [capture], segments: [segment] });

    expect(items[0]).toMatchObject({ kind: "note", note: line });
    expect(items[1]).toMatchObject({ kind: "capture", shot: capture });
    expect(items[2]).toMatchObject({ kind: "transcript", segment });
  });
});

describe("buildStream speaker names", () => {
  const shownFor = (items: ReturnType<typeof buildStream>) =>
    items.filter((i) => i.kind === "transcript").map((i) => i.kind === "transcript" && i.showSpeaker);

  it("shows who is speaking", () => {
    const items = build({
      segments: [seg({ startMs: 0, speaker: "Ada" }), seg({ startMs: 4_000, speaker: "Grace" })],
    });

    expect(shownFor(items)).toEqual([true, true]);
  });

  it("repeats a speaker's name only when it changes", () => {
    // A name on every line of a long turn is noise; the label marks where the speaker changes.
    const items = build({
      segments: [
        seg({ startMs: 0, speaker: "Ada" }),
        seg({ startMs: 4_000, speaker: "Ada" }),
        seg({ startMs: 8_000, speaker: "Grace" }),
      ],
    });

    expect(shownFor(items)).toEqual([true, false, true]);
  });

  it("does not repeat a name because a note or a capture came between two of one speaker's lines", () => {
    // This is the whole reason the comparison is against the previous TRANSCRIPT segment rather than
    // the previous stream row: writing a note mid-turn must not make Ada introduce herself again.
    const items = build({
      lines: [note({ capturedAtMs: 2_000 })],
      shots: [shot(3_000)],
      segments: [seg({ startMs: 0, speaker: "Ada" }), seg({ startMs: 4_000, speaker: "Ada" })],
    });

    expect(shownFor(items)).toEqual([true, false]);
  });

  it("leaves how a guessed name is rendered to the segment", () => {
    // The stream decides WHETHER to show a name; a suggestion's italic + "?" treatment stays on the
    // segment, so the row keeps it without this function knowing anything about identification.
    const items = build({
      segments: [seg({ startMs: 0, speaker: "Grace", speakerIsSuggestion: true })],
    });

    expect(items[0]).toMatchObject({ kind: "transcript", showSpeaker: true });
    expect(items[0].kind === "transcript" && items[0].segment.speakerIsSuggestion).toBe(true);
  });

  it("shows no speaker at all for a transcript recorded before speakers were stitched", () => {
    const items = build({ segments: [seg({ startMs: 0 })] });

    expect(shownFor(items)).toEqual([false]);
  });
});

describe("buildStream filters", () => {
  const input = {
    lines: [note({ capturedAtMs: 1_000 }), note({ capturedAtMs: 2_000, id: "n2" })],
    shots: [shot(3_000)],
    segments: [seg({ startMs: 4_000 })],
  };

  it("shows everything by default", () => {
    expect(build({ ...input, filter: "all" }).map((i) => i.kind)).toEqual([
      "note",
      "note",
      "capture",
      "transcript",
    ]);
  });

  it("narrows to notes", () => {
    const items = build({ ...input, filter: "notes" });

    expect(items).toHaveLength(2);
    expect(items.every((i) => i.kind === "note")).toBe(true);
  });

  it("narrows to captures", () => {
    expect(build({ ...input, filter: "captures" }).map((i) => i.kind)).toEqual(["capture"]);
  });

  it("keeps a filtered view in timeline order rather than in source order", () => {
    const items = build({
      lines: [note({ capturedAtMs: 8_000, id: "late" }), note({ capturedAtMs: 1_000, id: "early" })],
      filter: "notes",
    });

    expect(items.map((i) => i.id)).toEqual(["n:early", "n:late"]);
  });
});

describe("streamCounts", () => {
  it("counts the whole meeting, not the filtered view", () => {
    // The chips show live totals whichever one is selected - a "Notes 2" chip reading "Notes 0" while
    // Captures was chosen would be telling the user their notes had gone.
    const counts = streamCounts({
      lines: [note({ capturedAtMs: 1_000 }), note({ capturedAtMs: 2_000, id: "n2" })],
      shots: [shot(3_000)],
    });

    expect(counts).toEqual({ notes: 2, captures: 1 });
  });
});

describe("stampColumnPx", () => {
  it("fits mm:ss for the first hour", () => {
    expect(stampColumnPx(0)).toBe(34);
    expect(stampColumnPx(59 * 60_000 + 59_000)).toBe(34);
  });

  it("widens once the clock rolls over to h:mm:ss", () => {
    // formatDuration switches format at an hour; a column sized for "59:59" clips "1:00:00".
    expect(stampColumnPx(3_600_000)).toBe(50);
    expect(stampColumnPx(4_000_000)).toBe(50);
  });
});
