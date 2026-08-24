import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import SegmentSplitModal from "./SegmentSplitModal";
import { api } from "../../lib/api";
import type { SegmentDto, SpeakerInfo } from "../../lib/types";

vi.mock("../../lib/api", () => ({
  api: {
    getSegmentWords: vi.fn(),
    splitSegment: vi.fn(),
    assignSegmentSpeaker: vi.fn(),
    getRecording: vi.fn(),
  },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

const seg: SegmentDto = {
  id: "s1", speaker: "SPEAKER_00", speakerDisplay: "Ken", startMs: 900, endMs: 2600,
  original: "Hello world again", revised: null, text: "Hello world again", hasWords: true,
};

function speaker(label: string, displayName: string): SpeakerInfo {
  return {
    label, displayName, personId: null, title: null, companyName: null, email: null, phone: null,
    isInternal: null, identifiedAuto: false, isMultiSpeaker: false, embeddingStale: false,
  };
}

const speakers = [speaker("SPEAKER_00", "Ken"), speaker("SPEAKER_01", "Aidan")];

function render_(over: Partial<SegmentDto> = {}, onDone = vi.fn()) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <SegmentSplitModal
        recordingId="r1"
        seg={{ ...seg, ...over }}
        speakers={speakers}
        onClose={() => {}}
        onDone={onDone}
      />
    </QueryClientProvider>,
  );
  return onDone;
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.getSegmentWords).mockResolvedValue([
    { w: "Hello", s: 1000, e: 1400 },
    { w: "world", s: 1500, e: 1900 },
    { w: "again", s: 2100, e: 2500 },
  ]);
  mock(api.splitSegment).mockResolvedValue(undefined);
  mock(api.assignSegmentSpeaker).mockResolvedValue({ label: "SPEAKER_01", displayName: "Aidan" });
  mock(api.getRecording).mockResolvedValue({
    current: {
      segments: [
        { ...seg, id: "left", original: "Hello world", startMs: 900, endMs: 1900 },
        { ...seg, id: "right", original: "again", startMs: 2100, endMs: 2600 },
      ],
    },
  });
});

describe("SegmentSplitModal", () => {
  /// Three words means two interior gaps. A gap before the first word or after the last would leave a half
  /// empty, which the server rejects with a 409 - so it must not be offered at all.
  it("offers a cut point between each pair of words, and none at the ends", async () => {
    render_();
    await waitFor(() => expect(screen.getAllByRole("button", { name: /Split here/ })).toHaveLength(2));
  });

  it("splits at the chosen gap", async () => {
    const onDone = render_();

    const gaps = await screen.findAllByRole("button", { name: /Split here/ });
    await userEvent.click(gaps[1]); // before "again" -> wordIndex 2
    await userEvent.click(screen.getByRole("button", { name: "Split" }));

    await waitFor(() => expect(api.splitSegment).toHaveBeenCalledWith("r1", "s1", 2, false));
    await waitFor(() => expect(onDone).toHaveBeenCalled());
  });

  it("shows the two halves before committing to them", async () => {
    // The point of the preview is that you can see the cut landed where you meant before an action that
    // has no undo.
    render_();

    const gaps = await screen.findAllByRole("button", { name: /Split here/ });
    await userEvent.click(gaps[1]);

    const preview = screen.getByTestId("split-preview");
    expect(preview.textContent).toContain("Hello world");
    expect(preview.textContent).toContain("again");
  });

  it("moves the new half to the chosen speaker after splitting", async () => {
    render_();

    const gaps = await screen.findAllByRole("button", { name: /Split here/ });
    await userEvent.click(gaps[1]);
    await userEvent.selectOptions(screen.getByLabelText(/Speaker for the new part/), "SPEAKER_01");
    await userEvent.click(screen.getByRole("button", { name: "Split" }));

    // The right-hand half is the one that was cut off, so that is what gets reassigned.
    await waitFor(() => expect(api.assignSegmentSpeaker).toHaveBeenCalledWith("r1", "right", "SPEAKER_01"));
  });

  it("does not reassign when the new half keeps the same speaker", async () => {
    render_();

    const gaps = await screen.findAllByRole("button", { name: /Split here/ });
    await userEvent.click(gaps[1]);
    await userEvent.click(screen.getByRole("button", { name: "Split" }));

    await waitFor(() => expect(api.splitSegment).toHaveBeenCalled());
    // A pointless reassignment would mark both voiceprints stale for no reason.
    expect(api.assignSegmentSpeaker).not.toHaveBeenCalled();
  });

  it("asks before discarding an edit, and passes the flag only after confirming", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    render_({ revised: "my correction", text: "my correction" });

    const gaps = await screen.findAllByRole("button", { name: /Split here/ });
    await userEvent.click(gaps[0]);
    await userEvent.click(screen.getByRole("button", { name: "Split" }));

    expect(confirm).toHaveBeenCalled();
    await waitFor(() => expect(api.splitSegment).toHaveBeenCalledWith("r1", "s1", 1, true));
  });

  it("does not split when the discard confirmation is declined", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(false);
    render_({ revised: "my correction", text: "my correction" });

    const gaps = await screen.findAllByRole("button", { name: /Split here/ });
    await userEvent.click(gaps[0]);
    await userEvent.click(screen.getByRole("button", { name: "Split" }));

    // Flush a macrotask so a mistakenly-fired call would have landed by now. Asserting immediately would
    // pass before the call could have happened, which proves nothing.
    await new Promise((r) => setTimeout(r, 0));
    expect(api.splitSegment).not.toHaveBeenCalled();
  });

  it("cannot be confirmed until a cut point is chosen", async () => {
    render_();
    await screen.findAllByRole("button", { name: /Split here/ });

    expect(screen.getByRole("button", { name: "Split" }).hasAttribute("disabled")).toBe(true);
  });

  it("reports a failure instead of closing", async () => {
    mock(api.splitSegment).mockRejectedValue(new Error("boom"));
    const onDone = render_();

    const gaps = await screen.findAllByRole("button", { name: /Split here/ });
    await userEvent.click(gaps[0]);
    await userEvent.click(screen.getByRole("button", { name: "Split" }));

    expect(await screen.findByText(/Could not split this segment/)).toBeTruthy();
    expect(onDone).not.toHaveBeenCalled();
  });
});
