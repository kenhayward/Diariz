import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import PersonVoiceprintTab from "./PersonVoiceprintTab";
import { api } from "../lib/api";
import type { Person, VoiceSample } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    getPerson: vi.fn(),
    getRecording: vi.fn(),
    setVoiceSampleSpans: vi.fn(),
    removeVoiceSample: vi.fn(),
  },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function person(over: Partial<Person> = {}): Person {
  return {
    id: "p1", name: "Ada Lovelace", title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: true, sampleCount: 1,
    linkedUserId: null, isSelf: false, canManageBiometrics: true,
    createdAt: "2026-07-29T00:00:00Z", updatedAt: "2026-07-29T00:00:00Z", ...over,
  };
}

function sample(over: Partial<VoiceSample> = {}): VoiceSample {
  return {
    id: "vs1", recordingId: "r1", recordingName: "Standup", speakerLabel: "SPEAKER_00",
    startMs: 0, createdAt: "2026-07-29T00:00:00Z",
    selectedMs: 3000, usedMs: 3000, stale: false, pending: false, spans: [], ...over,
  };
}

/// Three segments for SPEAKER_00 and one for someone else, so the filter is exercised.
const segments = [
  { id: "g1", speaker: "SPEAKER_00", speakerDisplay: "Ada", startMs: 0, endMs: 1000, original: "One", revised: null, text: "One", hasWords: true },
  { id: "g2", speaker: "SPEAKER_00", speakerDisplay: "Ada", startMs: 1000, endMs: 2000, original: "Two", revised: null, text: "Two", hasWords: true },
  { id: "g3", speaker: "SPEAKER_00", speakerDisplay: "Ada", startMs: 2000, endMs: 3000, original: "Three", revised: null, text: "Three", hasWords: true },
  { id: "g4", speaker: "SPEAKER_01", speakerDisplay: "Bob", startMs: 3000, endMs: 4000, original: "Other", revised: null, text: "Other", hasWords: true },
];

function setup(p: Person = person()) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <PersonVoiceprintTab person={p} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.getPerson).mockResolvedValue({ person: person(), identifiedCount: 1, samples: [sample()] });
  mock(api.getRecording).mockResolvedValue({ current: { segments } });
  mock(api.setVoiceSampleSpans).mockResolvedValue(undefined);
  mock(api.removeVoiceSample).mockResolvedValue(undefined);
});

describe("PersonVoiceprintTab", () => {
  it("lists the recordings behind the voiceprint", async () => {
    expect(await screen.findByText("Standup").catch(() => null)).toBeNull();
    setup();
    expect(await screen.findByText("Standup")).toBeTruthy();
  });

  it("states what was used against what was selected, rather than implying it used it all", async () => {
    // The worker caps pooled audio. Showing only the selection would quietly promise something untrue.
    mock(api.getPerson).mockResolvedValue({
      person: person(), identifiedCount: 1,
      samples: [sample({ selectedMs: 252000, usedMs: 120000 })],
    });
    setup();

    expect(await screen.findByText(/Using 2:00 of the 4:12 selected/)).toBeTruthy();
  });

  it("shows a sample as recomputing while its job is in flight", async () => {
    mock(api.getPerson).mockResolvedValue({
      person: person(), identifiedCount: 1, samples: [sample({ pending: true, usedMs: null })],
    });
    setup();

    expect(await screen.findByText(/Recomputing/)).toBeTruthy();
  });

  it("flags a sample whose speaker's audio was re-attributed", async () => {
    mock(api.getPerson).mockResolvedValue({
      person: person(), identifiedCount: 1, samples: [sample({ stale: true })],
    });
    setup();

    expect(await screen.findByText(/Needs recomputing/)).toBeTruthy();
  });

  it("lists only that speaker's segments when expanded", async () => {
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    expect(await screen.findByRole("checkbox", { name: /One/ })).toBeTruthy();
    // A segment belonging to a different speaker is not this sample's audio.
    expect(screen.queryByRole("checkbox", { name: /Other/ })).toBeNull();
  });

  it("ticks every segment when nothing is selected", async () => {
    // No spans means the whole speaker, which is what an untouched voiceprint trains on.
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    const boxes = await screen.findAllByRole("checkbox");
    expect(boxes.every((b) => (b as HTMLInputElement).checked)).toBe(true);
  });

  it("ticks only the selected segments when spans are stored", async () => {
    mock(api.getPerson).mockResolvedValue({
      person: person(), identifiedCount: 1,
      samples: [sample({ spans: [{ startMs: 0, endMs: 1000 }] })],
    });
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    expect((await screen.findByRole("checkbox", { name: /One/ }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole("checkbox", { name: /Two/ }) as HTMLInputElement).checked).toBe(false);
  });

  it("batches a run of ticks into one recompute", async () => {
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    await userEvent.click(await screen.findByRole("checkbox", { name: /Two/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Three/ }));
    await userEvent.click(screen.getByRole("button", { name: /Recompute voiceprint/ }));

    expect(api.setVoiceSampleSpans).toHaveBeenCalledTimes(1);
    expect(api.setVoiceSampleSpans).toHaveBeenCalledWith("p1", "vs1", [{ startMs: 0, endMs: 1000 }]);
  });

  it("does not queue anything until Recompute is pressed", async () => {
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));
    await userEvent.click(await screen.findByRole("checkbox", { name: /Two/ }));

    // Flush a macrotask so a mistakenly-fired call would have landed. Asserting immediately would pass
    // before the call could have happened, which proves nothing.
    await new Promise((r) => setTimeout(r, 0));
    expect(api.setVoiceSampleSpans).not.toHaveBeenCalled();
  });

  it("sends an empty selection when everything is ticked, meaning the whole speaker", async () => {
    // Not a snapshot of today's segment boundaries: a re-transcribe would move them, and pinned spans
    // would then describe audio that no longer lines up.
    mock(api.getPerson).mockResolvedValue({
      person: person(), identifiedCount: 1,
      samples: [sample({ spans: [{ startMs: 0, endMs: 1000 }] })],
    });
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    await userEvent.click(await screen.findByRole("checkbox", { name: /Two/ }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Three/ }));
    await userEvent.click(screen.getByRole("button", { name: /Recompute voiceprint/ }));

    expect(api.setVoiceSampleSpans).toHaveBeenCalledWith("p1", "vs1", []);
  });

  it("cannot recompute with nothing ticked", async () => {
    // An empty tick set would be sent as "the whole speaker", which is the opposite of what unticking
    // everything looks like it should do. Refuse rather than surprise.
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    for (const box of await screen.findAllByRole("checkbox")) await userEvent.click(box);

    expect(screen.getByRole("button", { name: /Recompute voiceprint/ }).hasAttribute("disabled")).toBe(true);
  });

  it("explains and offers nothing for someone who opted out", async () => {
    setup(person({ voiceprintOptOut: true }));

    expect(await screen.findByText(/opted out of voice-printing/)).toBeTruthy();
    expect(api.getPerson).not.toHaveBeenCalled();
  });

  it("lists the samples but offers no controls without permission to manage biometrics", async () => {
    // Someone needs to be able to see what a voiceprint learned from even if they may not change it -
    // the same rule the opt-out checkbox follows.
    setup(person({ canManageBiometrics: false }));

    expect(await screen.findByText("Standup")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Recompute voiceprint/ })).toBeNull();
  });

  it("reports a failure instead of pretending the job was queued", async () => {
    mock(api.setVoiceSampleSpans).mockRejectedValue(new Error("boom"));
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));
    await userEvent.click(await screen.findByRole("checkbox", { name: /Two/ }));
    await userEvent.click(screen.getByRole("button", { name: /Recompute voiceprint/ }));

    await waitFor(() => expect(screen.getByText(/Could not queue the recompute/)).toBeTruthy());
  });
});
