import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import PersonDiagnosticsTab from "./PersonDiagnosticsTab";
import { api } from "../lib/api";
import type { Person, SampleDiagnosis } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { getPersonDiagnostics: vi.fn() },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function person(over: Partial<Person> = {}): Person {
  return {
    id: "p1", name: "Ada Lovelace", title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: true, sampleCount: 3,
    linkedUserId: null, isSelf: false, canManageBiometrics: true,
    createdAt: "2026-08-25T00:00:00Z", updatedAt: "2026-08-25T00:00:00Z", ...over,
  };
}

function sample(over: Partial<SampleDiagnosis> = {}): SampleDiagnosis {
  return {
    voiceSampleId: "vs1", speakerId: "sp1", recordingId: "r1", recordingName: "Standup",
    speakerLabel: "SPEAKER_00", nearestSiblingDistance: 0.1, distanceToOthers: 0.12,
    verdict: "Core", isTraining: true, ...over,
  };
}

function setup(p: Person = person()) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <PersonDiagnosticsTab person={p} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.getPersonDiagnostics).mockResolvedValue({
    samples: [sample()],
    aloneCount: 0,
    widestPair: 0.12,
  });
});

describe("PersonDiagnosticsTab", () => {
  it("says everything is consistent rather than showing an empty result", async () => {
    // "All fine" and "this did not load" must not look the same, or a silent failure reads as a clean bill
    // of health.
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [sample(), sample({ voiceSampleId: "vs2" })],
      aloneCount: 0,
      widestPair: 0.12,
    });
    setup();

    expect(await screen.findByText(/all resemble each other/i)).toBeTruthy();
  });

  it("counts the samples that resemble nothing", async () => {
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [sample(), sample({ voiceSampleId: "vs2", verdict: "Alone", nearestSiblingDistance: 0.7 })],
      aloneCount: 1,
      widestPair: 0.7,
    });
    setup();

    expect(await screen.findByText(/1 .*resembles? none/i)).toBeTruthy();
  });

  it("says there is nothing to compare when the person has one sample", async () => {
    // Most of the directory is in this state, and it is not a problem - saying nothing at all would leave
    // the tab looking broken.
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [sample({ verdict: "Only", nearestSiblingDistance: null, distanceToOthers: null })],
      aloneCount: 0,
      widestPair: null,
    });
    setup();

    expect(await screen.findByText(/only one recording/i)).toBeTruthy();
  });

  it("names the recording to go and listen to", async () => {
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [sample({ recordingName: "Client call", verdict: "Alone", nearestSiblingDistance: 0.7 })],
      aloneCount: 1,
      widestPair: 0.7,
    });
    setup();

    expect(await screen.findByText("Client call")).toBeTruthy();
    expect(screen.getByText(/resembles none of the others/i)).toBeTruthy();
  });

  it("puts the verdict in words rather than a bare distance", async () => {
    // A user cannot act on "0.62". They can act on being told which recording does not sound like the rest.
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [sample({ verdict: "Variant", nearestSiblingDistance: 0.35 })],
      aloneCount: 0,
      widestPair: 0.35,
    });
    setup();

    expect(await screen.findByText(/different recording condition/i)).toBeTruthy();
  });

  it("marks a sample that was already dropped from training", async () => {
    // Shown, not hidden: seeing that the one you dropped was the outlier confirms dropping it was right.
    mock(api.getPersonDiagnostics).mockResolvedValue({
      samples: [sample({ isTraining: false, verdict: "Only" })],
      aloneCount: 0,
      widestPair: null,
    });
    setup();

    expect(await screen.findByText(/not training/i)).toBeTruthy();
  });

  it("asks nothing for someone who opted out", async () => {
    setup(person({ voiceprintOptOut: true }));

    expect(await screen.findByText(/opted out of voice-printing/)).toBeTruthy();
    expect(api.getPersonDiagnostics).not.toHaveBeenCalled();
  });
});
