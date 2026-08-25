import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import PersonVoiceprintTab from "./PersonVoiceprintTab";
import { api } from "../lib/api";
import type { Person, PersonAttribution, SampleDiagnosis, VoiceSample } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    getPerson: vi.fn(),
    getRecording: vi.fn(),
    getPersonAttributions: vi.fn(),
    getAttributionSegments: vi.fn(),
    setAttributionTraining: vi.fn(),
    setVoiceSampleSpans: vi.fn(),
    removeVoiceSample: vi.fn(),
    personClip: vi.fn(),
    getPersonDiagnostics: vi.fn(),
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

function attribution(over: Partial<PersonAttribution> = {}): PersonAttribution {
  return {
    speakerId: "sp1", recordingId: "r1", recordingName: "Standup", speakerLabel: "SPEAKER_00",
    linkedBy: "manual", isTraining: true, voiceSampleId: "vs1", speechMs: 3000,
    canAccessRecording: true, stillLinked: true, canReassign: true, ...over,
  };
}

/// Only that speaker's segments - the endpoint never returns the rest of the transcript.
const attributionSegments = [
  { id: "g1", startMs: 0, endMs: 1000, text: "One" },
  { id: "g2", startMs: 1000, endMs: 2000, text: "Two" },
  { id: "g3", startMs: 2000, endMs: 3000, text: "Three" },
];

function diagnosis(over: Partial<SampleDiagnosis> = {}): SampleDiagnosis {
  return {
    voiceSampleId: "vs1", speakerId: "sp1", recordingId: "r1", recordingName: "Standup",
    speakerLabel: "SPEAKER_00", nearestSiblingDistance: 0.1, distanceToOthers: 0.12,
    verdict: "Core", isTraining: true, ...over,
  };
}

function setup(p: Person = person()) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <PersonVoiceprintTab person={p} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  // jsdom implements neither media playback nor object URLs, and the component legitimately uses both.
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
  window.HTMLMediaElement.prototype.pause = vi.fn();
  URL.createObjectURL = vi.fn(() => "blob:clip");
  URL.revokeObjectURL = vi.fn();
  mock(api.personClip).mockResolvedValue(new Blob(["RIFF"]));
  mock(api.getPerson).mockResolvedValue({ person: person(), identifiedCount: 1, samples: [sample()] });
  mock(api.getRecording).mockResolvedValue({ current: { segments } });
  mock(api.getPersonAttributions).mockResolvedValue([attribution()]);
  mock(api.getAttributionSegments).mockResolvedValue(attributionSegments);
  mock(api.setAttributionTraining).mockResolvedValue(undefined);
  mock(api.setVoiceSampleSpans).mockResolvedValue(undefined);
  mock(api.removeVoiceSample).mockResolvedValue(undefined);
  mock(api.getPersonDiagnostics).mockResolvedValue({ samples: [], aloneCount: 0, widestPair: null });
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

  it("renders the segments the endpoint returns", async () => {
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    expect(await screen.findByRole("checkbox", { name: /One/ })).toBeTruthy();
    // This used to also assert that another speaker's segment was absent, back when the client filtered a
    // whole transcript by speaker label. The endpoint now returns only this speaker's segments, so that
    // assertion could no longer fail here whatever the client did - the guarantee is asserted server-side
    // instead, by PeopleClipEndpointTests.Segments_returns_only_that_speakers_segments.
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


  it("lists a speaker that was recognised automatically and trains nothing", async () => {
    // The case the old sample-only list could not show at all: auto-identification links a speaker without
    // ever creating a voice sample, which is why the list read as arbitrary.
    mock(api.getPersonAttributions).mockResolvedValue([
      attribution({ linkedBy: "auto", isTraining: false, voiceSampleId: null }),
    ]);
    mock(api.getPerson).mockResolvedValue({ person: person(), identifiedCount: 1, samples: [] });
    setup();

    expect(await screen.findByText("Standup")).toBeTruthy();
    expect(screen.getByText(/Recognised automatically/)).toBeTruthy();
    const box = screen.getByRole("checkbox", { name: /Trains the voiceprint/ }) as HTMLInputElement;
    expect(box.checked).toBe(false);
  });

  it("adds a speaker to training when its box is ticked", async () => {
    mock(api.getPersonAttributions).mockResolvedValue([
      attribution({ linkedBy: "auto", isTraining: false, voiceSampleId: null }),
    ]);
    mock(api.getPerson).mockResolvedValue({ person: person(), identifiedCount: 1, samples: [] });
    setup();

    await userEvent.click(await screen.findByRole("checkbox", { name: /Trains the voiceprint/ }));

    await waitFor(() =>
      expect(api.setAttributionTraining).toHaveBeenCalledWith("p1", "sp1", true));
  });

  it("removes a speaker from training when its box is unticked", async () => {
    setup();

    await userEvent.click(await screen.findByRole("checkbox", { name: /Trains the voiceprint/ }));

    await waitFor(() =>
      expect(api.setAttributionTraining).toHaveBeenCalledWith("p1", "sp1", false));
  });

  it("offers no training toggle without permission to manage biometrics", async () => {
    setup(person({ canManageBiometrics: false }));

    expect(await screen.findByText("Standup")).toBeTruthy();
    expect(screen.queryByRole("checkbox", { name: /Trains the voiceprint/ })).toBeNull();
  });

  it("still lists a recording the caller cannot access, but offers nothing on it", async () => {
    // The directory is platform-wide while recordings are ownership-filtered. The row belongs in the list -
    // it is part of what trained the voiceprint - but there is no transcript or audio behind it.
    mock(api.getPersonAttributions).mockResolvedValue([attribution({ canAccessRecording: false })]);
    setup();

    expect(await screen.findByText("Standup")).toBeTruthy();
    expect(screen.getByText(/recording you cannot access/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Show segments/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /^Play/ })).toBeNull();
  });

  it("reads segments from the attribution endpoint, never the recording", async () => {
    // getRecording is ownership-filtered, so it 404s for exactly the cross-owner rows this tab must show -
    // and it would hand over every other speaker's words along with this one's.
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));

    await waitFor(() => expect(api.getAttributionSegments).toHaveBeenCalledWith("p1", "sp1"));
    expect(api.getRecording).not.toHaveBeenCalled();
  });

  it("plays one segment as a clip of exactly that span", async () => {
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));
    await screen.findByText("Two");

    await userEvent.click(screen.getAllByRole("button", { name: /Play segment/ })[1]);

    await waitFor(() => expect(api.personClip).toHaveBeenCalledWith("p1", "sp1", 1000, 2000));
  });

  it("reports a failure instead of pretending the job was queued", async () => {
    mock(api.setVoiceSampleSpans).mockRejectedValue(new Error("boom"));
    setup();
    await userEvent.click(await screen.findByRole("button", { name: /Show segments/ }));
    await userEvent.click(await screen.findByRole("checkbox", { name: /Two/ }));
    await userEvent.click(screen.getByRole("button", { name: /Recompute voiceprint/ }));

    await waitFor(() => expect(screen.getByText(/Could not queue the recompute/)).toBeTruthy());
  });

  it("says why a listed recording is not training the voiceprint", async () => {
    // Six of these exist on the live instance: the speaker was unassigned or reassigned on the transcript
    // and its sample kept training regardless. The row has to be here - invisible is how they survived -
    // but a row that is simply not ticked, with no reason given, reads as a bug rather than a fact.
    mock(api.getPersonAttributions).mockResolvedValue([
      attribution({ isTraining: false, stillLinked: false, canReassign: true }),
    ]);

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <PersonVoiceprintTab person={person()} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText(/no longer linked to this person/i)).toBeTruthy();
  });

  // ---- The merged list. Two tabs describing the same recordings differently is what made the
  // reported problem unactionable: the verdicts were on one and the controls on the other. ----

  it("describes the list it sits above, rather than counting something else", async () => {
    // The reported contradiction: a header reading "5 recordings resemble none of the others" over a
    // list whose rows said "Matches the others". Both were true - the header counted only outliers
    // while the list showed every sample - and together they read as a bug.
    mock(api.getPersonAttributions).mockResolvedValue([
      attribution({ speakerId: "sp1", isTraining: true }),
      attribution({ speakerId: "sp2", recordingName: "Retro", voiceSampleId: null, isTraining: false }),
    ]);
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [diagnosis({ speakerId: "sp1", verdict: "Alone", nearestSiblingDistance: 0.82 })],
      aloneCount: 1,
      widestPair: 0.82,
    });
    setup();

    expect(await screen.findByText(/Trained on 1 of 2 recordings/)).toBeTruthy();
    expect(screen.getByText(/1 sounds unlike the rest/)).toBeTruthy();
  });

  it("puts the recording worth listening to at the top", async () => {
    // In the live report the row that mattered was third, under two healthy ones.
    mock(api.getPersonAttributions).mockResolvedValue([
      attribution({ speakerId: "sp1", recordingName: "Alpha" }),
      attribution({ speakerId: "sp2", recordingName: "Bravo", voiceSampleId: "vs2" }),
      attribution({ speakerId: "sp3", recordingName: "Charlie", voiceSampleId: "vs3" }),
    ]);
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [
        diagnosis({ speakerId: "sp1", verdict: "Core" }),
        diagnosis({ speakerId: "sp2", voiceSampleId: "vs2", verdict: "Core" }),
        diagnosis({ speakerId: "sp3", voiceSampleId: "vs3", verdict: "Alone", nearestSiblingDistance: 0.82 }),
      ],
      aloneCount: 1,
      widestPair: 0.82,
    });
    setup();

    await screen.findByText("Charlie");
    const names = screen.getAllByText(/Alpha|Bravo|Charlie/).map((n) => n.textContent);
    expect(names[0]).toBe("Charlie");
  });

  it("shows how alike two voices are, not how far apart", async () => {
    // A 0.82 distance is an 18% match. Printed raw it was the biggest number on the screen, sitting on
    // the worst row in the directory.
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [diagnosis({ verdict: "Alone", nearestSiblingDistance: 0.82, distanceToOthers: 0.86 })],
      aloneCount: 1,
      widestPair: 0.82,
    });
    setup();

    expect(await screen.findByText(/closest match 18%/)).toBeTruthy();
    expect(screen.getByText(/match to the rest 14%/)).toBeTruthy();
  });

  it("can narrow a long list to the ones worth checking", async () => {
    mock(api.getPersonAttributions).mockResolvedValue([
      attribution({ speakerId: "sp1", recordingName: "Alpha" }),
      attribution({ speakerId: "sp2", recordingName: "Bravo", voiceSampleId: "vs2" }),
    ]);
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [
        diagnosis({ speakerId: "sp1", verdict: "Core" }),
        diagnosis({ speakerId: "sp2", voiceSampleId: "vs2", verdict: "Alone", nearestSiblingDistance: 0.82 }),
      ],
      aloneCount: 1,
      widestPair: 0.82,
    });
    setup();
    await screen.findByText("Alpha");

    await userEvent.click(screen.getByRole("checkbox", { name: /only show the ones worth checking/i }));

    expect(screen.queryByText("Alpha")).toBeNull();
    expect(screen.getByText("Bravo")).toBeTruthy();
  });

  it("offers no filter when there is nothing to filter to", async () => {
    // A control that can only ever empty the list is worse than no control.
    setup();
    await screen.findByText("Standup");

    expect(screen.queryByRole("checkbox", { name: /worth checking/i })).toBeNull();
  });

  it("says there is nothing to compare when only one recording trains the voiceprint", async () => {
    // Most of the directory is in this state and it is not a problem. Showing a comparison it cannot
    // make would be worse than saying so.
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [diagnosis({ verdict: "Only", nearestSiblingDistance: null, distanceToOthers: null })],
      aloneCount: 0,
      widestPair: null,
    });
    setup();

    expect(await screen.findByText(/nothing to compare it with/i)).toBeTruthy();
  });

  // ---- Play voice. Reported directly: greyed out reads as broken rather than unavailable, and
  // collapsing the list mid-clip left the button saying "Stop" while the audio carried on. ----

  it("hides Play voice while it cannot work, rather than greying it out", async () => {
    // Asserted as absent, not as disabled: a disabled-attribute assertion would pass against the very
    // code being replaced.
    setup();
    await screen.findByText("Standup");

    expect(screen.queryByRole("button", { name: "Play voice" })).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Show segments" }));

    expect(await screen.findByRole("button", { name: "Play voice" })).toBeTruthy();
  });

  it("stops the audio when the segment list is collapsed", async () => {
    // The button vanishes with the list, so without this the clip plays on with nothing on screen able
    // to stop it.
    setup();
    await screen.findByText("Standup");
    await userEvent.click(screen.getByRole("button", { name: "Show segments" }));
    await userEvent.click(await screen.findByRole("button", { name: "Play voice" }));
    await waitFor(() => expect(api.personClip).toHaveBeenCalled());

    await userEvent.click(screen.getByRole("button", { name: "Hide segments" }));

    expect(window.HTMLMediaElement.prototype.pause).toHaveBeenCalled();
  });
});
