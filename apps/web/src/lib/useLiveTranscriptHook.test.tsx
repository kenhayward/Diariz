import { describe, expect, it, vi, beforeEach } from "vitest";
import { renderHook, act, waitFor } from "@testing-library/react";
import { useLiveTranscript } from "./useLiveTranscript";
import { api } from "./api";

vi.mock("./api", () => ({ api: { getRecording: vi.fn() } }));

const RECORDING = "rec-1";

function detail(recordingId: string, texts: string[]) {
  return {
    id: recordingId,
    status: "Live",
    speakers: [{ label: "SPEAKER_00", displayName: "Ada", suggestedPersonId: null }],
    current: {
      id: "t1",
      version: 1,
      isProvisional: true,
      segments: texts.map((text, i) => ({
        id: `s${i}`,
        speaker: "SPEAKER_00",
        speakerDisplay: "Ada",
        startMs: i * 3000,
        endMs: i * 3000 + 2500,
        original: text,
        revised: null,
      })),
    },
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
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(
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
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(
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
