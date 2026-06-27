import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listSpeakerProfiles: vi.fn(),
    getSpeakerProfile: vi.fn(),
    renameSpeakerProfile: vi.fn(),
    removeProfileContribution: vi.fn(),
    mergeSpeakerProfiles: vi.fn(),
    deleteSpeakerProfile: vi.fn(),
    deleteAllSpeakerProfiles: vi.fn(),
    audioUrl: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import PeopleModal from "./PeopleModal";
import type { SpeakerProfile, SpeakerProfileDetail } from "../lib/types";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;
const render_ = () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <PeopleModal onClose={() => {}} />
    </QueryClientProvider>,
  );
};

const people: SpeakerProfile[] = [
  { id: "p1", name: "Alice", sampleCount: 2 },
  { id: "p2", name: "Bob", sampleCount: 1 },
];

describe("PeopleModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.listSpeakerProfiles).mockResolvedValue(people);
    mock(api.renameSpeakerProfile).mockResolvedValue(undefined);
    mock(api.mergeSpeakerProfiles).mockResolvedValue(undefined);
    mock(api.deleteSpeakerProfile).mockResolvedValue(undefined);
    mock(api.deleteAllSpeakerProfiles).mockResolvedValue(undefined);
    mock(api.removeProfileContribution).mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
  });

  it("lists the enrolled people", async () => {
    render_();
    expect(await screen.findByRole("button", { name: "Alice" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Bob" })).toBeTruthy();
  });

  it("renames a person", async () => {
    render_();
    fireEvent.click(await screen.findByRole("button", { name: "Alice" }));
    const input = screen.getByLabelText("Rename Alice");
    fireEvent.change(input, { target: { value: "Alice Smith" } });
    fireEvent.keyDown(input, { key: "Enter" });

    await waitFor(() => expect(api.renameSpeakerProfile).toHaveBeenCalledWith("p1", "Alice Smith"));
  });

  it("merges another person in (target, source)", async () => {
    render_();
    await screen.findByRole("button", { name: "Alice" });
    // On Alice's row, the merge dropdown lists Bob as a source.
    fireEvent.change(screen.getByLabelText("Merge a person into Alice"), { target: { value: "p2" } });

    await waitFor(() => expect(api.mergeSpeakerProfiles).toHaveBeenCalledWith("p1", "p2"));
  });

  it("deletes a person", async () => {
    render_();
    await screen.findByRole("button", { name: "Alice" });
    fireEvent.click(screen.getAllByRole("button", { name: /^delete$/i })[0]);

    await waitFor(() => expect(api.deleteSpeakerProfile).toHaveBeenCalledWith("p1"));
  });

  it("erases all voiceprints", async () => {
    render_();
    fireEvent.click(await screen.findByRole("button", { name: /erase all voiceprints/i }));

    await waitFor(() => expect(api.deleteAllSpeakerProfiles).toHaveBeenCalled());
  });

  it("expands a person and removes a training contribution", async () => {
    const detail: SpeakerProfileDetail = {
      id: "p1",
      name: "Alice",
      sampleCount: 2,
      identifiedCount: 3,
      contributions: [
        { id: "c1", recordingId: "r1", recordingName: "Team Sync", speakerLabel: "SPEAKER_00", startMs: 3000, createdAt: "2026-06-27T00:00:00Z" },
      ],
    };
    mock(api.getSpeakerProfile).mockResolvedValue(detail);
    render_();

    fireEvent.click(await screen.findByLabelText("Expand Alice"));
    fireEvent.click(await screen.findByLabelText("Remove training sample from Team Sync"));

    await waitFor(() => expect(api.removeProfileContribution).toHaveBeenCalledWith("p1", "c1"));
  });

  it("plays a training sample (resolves the recording's audio and seeks)", async () => {
    const detail: SpeakerProfileDetail = {
      id: "p1",
      name: "Alice",
      sampleCount: 1,
      identifiedCount: 1,
      contributions: [
        { id: "c1", recordingId: "r1", recordingName: "Team Sync", speakerLabel: "SPEAKER_00", startMs: 3000, createdAt: "2026-06-27T00:00:00Z" },
      ],
    };
    mock(api.getSpeakerProfile).mockResolvedValue(detail);
    mock(api.audioUrl).mockResolvedValue("blob:audio");
    const play = vi.spyOn(window.HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    render_();

    fireEvent.click(await screen.findByLabelText("Expand Alice"));
    fireEvent.click(await screen.findByLabelText(/play sample from team sync/i));

    await waitFor(() => expect(api.audioUrl).toHaveBeenCalledWith("r1"));
    await waitFor(() => expect(play).toHaveBeenCalled());
  });
});
