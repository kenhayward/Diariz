import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import LiveTranscriptPanel from "./LiveTranscriptPanel";
import { emptyLiveTranscript, applyAppend, type LiveTranscript } from "../../lib/liveTranscript";

const RECORDING = "rec-1";

function withLines(
  ...lines: {
    startMs: number;
    text: string;
    sequence: number;
    speaker?: string;
    speakerIsSuggestion?: boolean;
  }[]
): LiveTranscript {
  let s = emptyLiveTranscript(RECORDING);
  // Grouped by chunk, because applyAppend REPLACES a sequence rather than adding to it - feeding lines
  // that share a sequence one at a time would leave only the last of them.
  for (const sequence of [...new Set(lines.map((l) => l.sequence))]) {
    s = applyAppend(s, {
      recordingId: RECORDING,
      sequence,
      segments: lines
        .filter((l) => l.sequence === sequence)
        .map((l) => ({
          id: `${l.sequence}-${l.startMs}`,
          startMs: l.startMs,
          endMs: l.startMs + 3000,
          text: l.text,
          sequence: l.sequence,
          speaker: l.speaker,
          speakerIsSuggestion: l.speakerIsSuggestion,
        })),
    });
  }
  return s;
}

describe("LiveTranscriptPanel", () => {
  it("renders the transcript as it arrives, in meeting order", () => {
    render(
      <LiveTranscriptPanel
        transcript={withLines(
          { startMs: 30_000, text: "the warehouse integration", sequence: 1 },
          { startMs: 0, text: "shall we make a start", sequence: 0 },
        )}
        lagSeconds={0}
        degraded={false}
      />,
    );

    const lines = screen.getAllByTestId("live-transcript-line").map((n) => n.textContent);
    expect(lines).toEqual(["shall we make a start", "the warehouse integration"]);
  });

  it("shows who is speaking", () => {
    // Turned on here and not before. Until the server stitched each chunk's voices onto one identity per
    // meeting, a label was only meaningful inside the thirty seconds it came from - SPEAKER_00 in chunk 3
    // had no relationship to SPEAKER_00 in chunk 4 - so showing them raw would have had speakers
    // reshuffling constantly, which reads as though it means something. The guard that enforced that
    // silence lived here and was deleted in the change that made labels stable.
    render(
      <LiveTranscriptPanel
        transcript={withLines(
          { startMs: 0, text: "shall we make a start", sequence: 0, speaker: "Ada" },
          { startMs: 4000, text: "the warehouse integration", sequence: 0, speaker: "Grace" },
        )}
        lagSeconds={0}
        degraded={false}
      />,
    );

    const speakers = screen.getAllByTestId("live-transcript-speaker").map((n) => n.textContent);
    expect(speakers).toEqual(["Ada", "Grace"]);
  });

  it("marks a suggested name as a guess rather than stating it", () => {
    // A borderline match is the server asking, not answering. Rendering it identically to a confident
    // one would put a coin flip on screen with the same authority as a name somebody confirmed.
    render(
      <LiveTranscriptPanel
        transcript={withLines(
          { startMs: 0, text: "certain", sequence: 0, speaker: "Ada" },
          { startMs: 4000, text: "unsure", sequence: 0, speaker: "Grace", speakerIsSuggestion: true },
        )}
        lagSeconds={0}
        degraded={false}
      />,
    );

    const [confident, guess] = screen.getAllByTestId("live-transcript-speaker");
    expect(guess.getAttribute("data-suggestion")).toBe("true");
    expect(confident.getAttribute("data-suggestion")).not.toBe("true");
  });

  it("repeats a speaker's name only when it changes", () => {
    // A name on every line of a long turn is noise. The transcript reads as a conversation, so the label
    // marks where the speaker changes.
    render(
      <LiveTranscriptPanel
        transcript={withLines(
          { startMs: 0, text: "first", sequence: 0, speaker: "Ada" },
          { startMs: 4000, text: "still me", sequence: 0, speaker: "Ada" },
          { startMs: 8000, text: "now me", sequence: 0, speaker: "Grace" },
        )}
        lagSeconds={0}
        degraded={false}
      />,
    );

    expect(screen.getAllByTestId("live-transcript-speaker").map((n) => n.textContent))
      .toEqual(["Ada", "Grace"]);
  });

  it("renders a line with no speaker at all", () => {
    // A transcript recorded before speakers were stitched, and the first moments of one where the
    // stitcher has not answered yet. Neither should render an empty label or crash the panel.
    render(
      <LiveTranscriptPanel
        transcript={withLines({ startMs: 0, text: "anonymous", sequence: 0 })}
        lagSeconds={0}
        degraded={false}
      />,
    );

    expect(screen.getByText("anonymous")).toBeTruthy();
    expect(screen.queryByTestId("live-transcript-speaker")).toBeNull();
  });

  it("says the transcript is still being written", () => {
    // The text on screen is provisional and will be replaced by the final pass. Presenting it as
    // finished would invite someone to copy it out as the record.
    render(
      <LiveTranscriptPanel transcript={withLines({ startMs: 0, text: "hello", sequence: 0 })}
        lagSeconds={0} degraded={false} />,
    );

    expect(screen.getByTestId("live-transcript-status")).toBeTruthy();
  });

  it("reports how far behind it is once there is a lag worth showing", () => {
    render(
      <LiveTranscriptPanel transcript={withLines({ startMs: 0, text: "hello", sequence: 0 })}
        lagSeconds={25} degraded={false} />,
    );

    expect(screen.getByTestId("live-transcript-status").textContent).toMatch(/25/);
  });

  it("explains a degraded transcript rather than showing an error", () => {
    // Falling behind costs the running commentary and nothing else: capture continues and the full
    // transcript arrives when the meeting ends. Saying so is the difference between a feature that
    // looks broken and one that looks busy.
    render(
      <LiveTranscriptPanel transcript={withLines({ startMs: 0, text: "hello", sequence: 0 })}
        lagSeconds={0} degraded />,
    );

    const status = screen.getByTestId("live-transcript-status").textContent ?? "";
    expect(status.toLowerCase()).toContain("after the meeting");
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says nothing has been said yet rather than showing an empty box", () => {
    render(<LiveTranscriptPanel transcript={emptyLiveTranscript(RECORDING)} lagSeconds={0} degraded={false} />);

    expect(screen.getByTestId("live-transcript-empty")).toBeTruthy();
    expect(screen.queryAllByTestId("live-transcript-line")).toHaveLength(0);
  });
});
