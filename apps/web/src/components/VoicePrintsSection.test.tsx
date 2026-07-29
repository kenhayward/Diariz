import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listPeople: vi.fn(),
    getPerson: vi.fn(),
    updatePerson: vi.fn(),
    removeVoiceSample: vi.fn(),
    mergePeople: vi.fn(),
    deletePerson: vi.fn(),
    deleteAllVoiceprints: vi.fn(),
    audioUrl: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import VoicePrintsSection from "./VoicePrintsSection";
import type { Person, PersonDetail } from "../lib/types";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;
const render_ = () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <VoicePrintsSection />
    </QueryClientProvider>,
  );
};

function person(id: string, name: string, sampleCount = 0): Person {
  return {
    id, name, title: null, companyName: null, email: null, phone: null,
    isInternal: false, voiceprintOptOut: false, hasVoiceprint: sampleCount > 0, sampleCount,
    linkedUserId: null, isSelf: false, canManageBiometrics: true,
    createdAt: "2026-07-29T00:00:00Z", updatedAt: "2026-07-29T00:00:00Z",
  };
}

const people: Person[] = [person("p1", "Alice", 2), person("p2", "Bob", 1)];

describe("VoicePrintsSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.listPeople).mockResolvedValue(people);
    mock(api.updatePerson).mockResolvedValue(undefined);
    mock(api.mergePeople).mockResolvedValue(undefined);
    mock(api.deletePerson).mockResolvedValue(undefined);
    mock(api.deleteAllVoiceprints).mockResolvedValue(undefined);
    mock(api.removeVoiceSample).mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
  });

  it("lists the enrolled people", async () => {
    render_();
    expect(await screen.findByRole("button", { name: "Alice" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Bob" })).toBeTruthy();
  });

  it("renders people as a table with a column header row", async () => {
    render_();
    await screen.findByRole("button", { name: "Alice" });
    expect(screen.getByRole("columnheader", { name: /name/i })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: /samples/i })).toBeTruthy();
  });

  it("renames a person", async () => {
    render_();
    fireEvent.click(await screen.findByRole("button", { name: "Alice" }));
    const input = screen.getByLabelText("Rename Alice");
    fireEvent.change(input, { target: { value: "Alice Smith" } });
    fireEvent.keyDown(input, { key: "Enter" });

    await waitFor(() => expect(api.updatePerson).toHaveBeenCalledWith("p1", { name: "Alice Smith" }));
  });

  it("merges another person in (target, source)", async () => {
    render_();
    await screen.findByRole("button", { name: "Alice" });
    // On Alice's row, the merge dropdown lists Bob as a source.
    fireEvent.change(screen.getByLabelText("Merge a person into Alice"), { target: { value: "p2" } });

    await waitFor(() => expect(api.mergePeople).toHaveBeenCalledWith("p1", "p2"));
  });

  it("deletes a person", async () => {
    render_();
    await screen.findByRole("button", { name: "Alice" });
    fireEvent.click(screen.getAllByRole("button", { name: /^delete$/i })[0]);

    await waitFor(() => expect(api.deletePerson).toHaveBeenCalledWith("p1"));
  });

  it("erases all voiceprints", async () => {
    render_();
    fireEvent.click(await screen.findByRole("button", { name: /erase all voiceprints/i }));

    await waitFor(() => expect(api.deleteAllVoiceprints).toHaveBeenCalled());
  });

  it("expands a person and removes a training contribution", async () => {
    const detail: PersonDetail = {
      person: person("p1", "Alice", 2),
      identifiedCount: 3,
      samples: [
        { id: "c1", recordingId: "r1", recordingName: "Team Sync", speakerLabel: "SPEAKER_00", startMs: 3000, createdAt: "2026-06-27T00:00:00Z" },
      ],
    };
    mock(api.getPerson).mockResolvedValue(detail);
    render_();

    fireEvent.click(await screen.findByLabelText("Expand Alice"));
    fireEvent.click(await screen.findByLabelText("Remove training sample from Team Sync"));

    await waitFor(() => expect(api.removeVoiceSample).toHaveBeenCalledWith("p1", "c1"));
  });

  it("plays a training sample (resolves the recording's audio and seeks)", async () => {
    const detail: PersonDetail = {
      id: "p1",
      name: "Alice",
      sampleCount: 1,
      identifiedCount: 1,
      samples: [
        { id: "c1", recordingId: "r1", recordingName: "Team Sync", speakerLabel: "SPEAKER_00", startMs: 3000, createdAt: "2026-06-27T00:00:00Z" },
      ],
    };
    mock(api.getPerson).mockResolvedValue(detail);
    mock(api.audioUrl).mockResolvedValue("blob:audio");
    const play = vi.spyOn(window.HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    render_();

    fireEvent.click(await screen.findByLabelText("Expand Alice"));
    fireEvent.click(await screen.findByLabelText(/play sample from team sync/i));

    await waitFor(() => expect(api.audioUrl).toHaveBeenCalledWith("r1"));
    await waitFor(() => expect(play).toHaveBeenCalled());
  });
});
