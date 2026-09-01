import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import LiveTranscriptPanel from "./LiveTranscriptPanel";
import { emptyLiveTranscript, applyAppend, type LiveTranscript } from "../../lib/liveTranscript";

const RECORDING = "rec-1";

function withLines(...lines: { startMs: number; text: string; sequence: number }[]): LiveTranscript {
  let s = emptyLiveTranscript(RECORDING);
  for (const l of lines) {
    s = applyAppend(s, {
      recordingId: RECORDING,
      sequence: l.sequence,
      segments: [{ id: `${l.sequence}-${l.startMs}`, startMs: l.startMs, endMs: l.startMs + 3000, text: l.text, sequence: l.sequence }],
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

  it("shows no speaker labels", () => {
    // THE guard for the phase 2 / phase 3 split, and the reason this test exists rather than being
    // implied. A diarization label is only meaningful within one chunk, so SPEAKER_00 in chunk 3 has
    // no relationship to SPEAKER_00 in chunk 4 - showing them raw would have speakers reshuffling
    // every thirty seconds, which reads as though it means something and is worse than saying
    // nothing. Labels are turned on when they can be made stable, not before.
    //
    // Delete this test in the SAME change that makes labels meaningful. Deleting it earlier removes
    // the guard while it still matters.
    render(
      <LiveTranscriptPanel
        transcript={withLines({ startMs: 0, text: "shall we make a start", sequence: 0 })}
        lagSeconds={0}
        degraded={false}
      />,
    );

    expect(screen.queryByText(/SPEAKER_/)).toBeNull();
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
