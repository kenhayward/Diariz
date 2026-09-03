import { describe, expect, it, vi, beforeEach } from "vitest";
import { renderHook, act, waitFor } from "@testing-library/react";
import { useLiveTranscript } from "./useLiveTranscript";
import { api } from "./api";

vi.mock("./api", () => ({ api: { getLiveTranscript: vi.fn() } }));

const RECORDING = "rec-1";

/// What the narrow live-transcript endpoint returns: the lines, already resolved. The speaker name and
/// its suggestion flag are the server's answer now - this used to be a whole recording detail that the
/// hook joined against a speaker list itself.
function detail(recordingId: string, texts: string[]) {
  return {
    recordingId,
    segments: texts.map((text, i) => ({
      id: `s${i}`,
      startMs: i * 3000,
      endMs: i * 3000 + 2500,
      text,
      speaker: "Ada",
      speakerIsSuggestion: false,
    })),
  };
}

/// The hook driven the way the recorder actually drives it.
///
/// The recorder mounts long before anyone presses Record, so the hook's first render ALWAYS has a null
/// recording and only later receives one. Every earlier test called `nextLiveState` directly with a
/// transcript that already knew its recording id, so none of them could see what happens across that
/// transition - which is where the reported bugs lived.
describe("useLiveTranscript, driven the way the recorder drives it", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows text for a recording that started AFTER the hook mounted", async () => {
    (api.getLiveTranscript as ReturnType<typeof vi.fn>).mockResolvedValue(
      detail(RECORDING, ["shall we start"]),
    );

    const { result, rerender } = renderHook(({ id }) => useLiveTranscript(id, () => 0), {
      initialProps: { id: null as string | null },
    });

    rerender({ id: RECORDING });
    await act(async () => {
      await result.current.onAppend({ recordingId: RECORDING, sequence: 0 });
    });

    await waitFor(() =>
      expect(result.current.transcript?.segments.map((s) => s.text)).toEqual(["shall we start"]),
    );
  });

  it("carries the speaker and whether the name is only a guess", async () => {
    // The server resolves both; the panel renders a guess in italics with a trailing "?" rather than
    // stating it. Nothing else joins those two ends, so dropping either here would leave a coin flip
    // presented with the authority of a confirmed name.
    (api.getLiveTranscript as ReturnType<typeof vi.fn>).mockResolvedValue({
      recordingId: RECORDING,
      segments: [
        { id: "s0", startMs: 0, endMs: 2500, text: "certain", speaker: "Ada", speakerIsSuggestion: false },
        { id: "s1", startMs: 3000, endMs: 5500, text: "unsure", speaker: "Grace", speakerIsSuggestion: true },
        { id: "s2", startMs: 6000, endMs: 8500, text: "nobody", speaker: null, speakerIsSuggestion: false },
      ],
    });

    const { result, rerender } = renderHook(({ id }) => useLiveTranscript(id, () => 0), {
      initialProps: { id: null as string | null },
    });

    rerender({ id: RECORDING });
    await act(async () => {
      await result.current.onAppend({ recordingId: RECORDING, sequence: 0 });
    });

    await waitFor(() => expect(result.current.transcript?.segments).toHaveLength(3));
    const segs = result.current.transcript!.segments;
    expect(segs.map((s) => s.speaker)).toEqual(["Ada", "Grace", undefined]);
    expect(segs[1].speakerIsSuggestion).toBe(true);
    expect(segs[0].speakerIsSuggestion).toBeFalsy();
  });

  it("marks itself degraded for a recording that started after mount", async () => {
    // Degraded events are filtered on the recording id held in STATE, not the one the hook was given.
    // Seeded once from a null recording, that id stays "" for the whole meeting and every degraded
    // notice is silently dropped - so the panel goes on claiming it is transcribing while the server
    // has stopped, which is precisely the case the notice exists to report.
    const { result, rerender } = renderHook(({ id }) => useLiveTranscript(id, () => 0), {
      initialProps: { id: null as string | null },
    });

    rerender({ id: RECORDING });
    act(() => result.current.onDegraded({ recordingId: RECORDING, sequence: 4 }));

    await waitFor(() => expect(result.current.degraded).toBe(true));
  });

  it("clears the previous meeting's text when a second recording starts", async () => {
    (api.getLiveTranscript as ReturnType<typeof vi.fn>).mockResolvedValue(
      detail(RECORDING, ["from the first meeting"]),
    );

    const { result, rerender } = renderHook(({ id }) => useLiveTranscript(id, () => 0), {
      initialProps: { id: null as string | null },
    });

    rerender({ id: RECORDING });
    await act(async () => {
      await result.current.onAppend({ recordingId: RECORDING, sequence: 0 });
    });
    await waitFor(() => expect(result.current.transcript?.segments).toHaveLength(1));

    rerender({ id: "rec-2" });

    await waitFor(() => expect(result.current.transcript?.segments).toEqual([]));
  });
});
