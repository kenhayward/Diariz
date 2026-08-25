import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import SpeakerSuggestionPrompt from "./SpeakerSuggestionPrompt";
import { api } from "../../lib/api";
import type { SpeakerInfo } from "../../lib/types";

vi.mock("../../lib/api", () => ({
  api: {
    acceptSpeakerSuggestion: vi.fn(),
    rejectSpeakerSuggestion: vi.fn(),
  },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function speaker(over: Partial<SpeakerInfo> = {}): SpeakerInfo {
  return {
    label: "SPEAKER_00", displayName: "SPEAKER_00", personId: null,
    title: null, companyName: null, email: null, phone: null, isInternal: null,
    identifiedAuto: false, isMultiSpeaker: false, embeddingStale: false,
    suggestedPersonId: "p1", suggestedPersonName: "Ada Lovelace", suggestedDistance: 0.35,
    ...over,
  };
}

function setup(info: SpeakerInfo = speaker(), onDecided = vi.fn()) {
  render(<SpeakerSuggestionPrompt speakerId="sp1" info={info} onDecided={onDecided} />);
  return onDecided;
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.acceptSpeakerSuggestion).mockResolvedValue(undefined);
  mock(api.rejectSpeakerSuggestion).mockResolvedValue(undefined);
});

describe("SpeakerSuggestionPrompt", () => {
  it("names who it thinks this is", () => {
    setup();

    expect(screen.getByText(/Ada Lovelace/)).toBeTruthy();
  });

  it("renders nothing when there is no suggestion", () => {
    // Every speaker row would otherwise carry an empty prompt, which is worse than no prompt at all.
    const { container } = render(
      <SpeakerSuggestionPrompt
        speakerId="sp1"
        info={speaker({ suggestedPersonId: null, suggestedPersonName: null, suggestedDistance: null })}
        onDecided={vi.fn()}
      />,
    );

    expect(container.textContent).toBe("");
  });

  it("confirms the suggestion", async () => {
    const onDecided = setup();

    await userEvent.click(screen.getByRole("button", { name: /Yes/ }));

    await waitFor(() => expect(api.acceptSpeakerSuggestion).toHaveBeenCalledWith("sp1"));
    expect(onDecided).toHaveBeenCalled();
  });

  it("declines the suggestion", async () => {
    const onDecided = setup();

    await userEvent.click(screen.getByRole("button", { name: /No/ }));

    await waitFor(() => expect(api.rejectSpeakerSuggestion).toHaveBeenCalledWith("sp1"));
    expect(onDecided).toHaveBeenCalled();
  });

  it("does not report a decision the server refused", async () => {
    // Reporting one would remove the prompt from the transcript while the suggestion is still pending on the
    // server, and it would reappear on the next load with no explanation.
    mock(api.rejectSpeakerSuggestion).mockRejectedValue(new Error("boom"));
    const onDecided = setup();

    await userEvent.click(screen.getByRole("button", { name: /No/ }));

    await waitFor(() => expect(screen.getByText(/Could not save/)).toBeTruthy());
    expect(onDecided).not.toHaveBeenCalled();
  });

  it("cannot be answered twice while the first answer is in flight", async () => {
    // A double click would otherwise log two decisions for one judgement, and the sweep would count the
    // same evidence twice.
    let release: (() => void) | undefined;
    mock(api.acceptSpeakerSuggestion).mockReturnValue(new Promise<void>((r) => { release = () => r(); }));
    setup();

    const yes = screen.getByRole("button", { name: /Yes/ });
    await userEvent.click(yes);
    await userEvent.click(yes);

    expect(mock(api.acceptSpeakerSuggestion).mock.calls).toHaveLength(1);
    release?.();
  });
});
