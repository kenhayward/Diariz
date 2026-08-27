import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import VoicesToConfirmModal from "./VoicesToConfirmModal";
import { api } from "../lib/api";
import type { AttributionSegment, SpeakerSuggestion } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    getSpeakerSuggestions: vi.fn(),
    getSuggestionSegments: vi.fn(),
    suggestionClip: vi.fn(),
    acceptSpeakerSuggestion: vi.fn(),
    rejectSpeakerSuggestion: vi.fn(),
  },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function suggestion(over: Partial<SpeakerSuggestion> = {}): SpeakerSuggestion {
  return {
    speakerId: "sp1", recordingId: "r1", recordingName: "Standup", speakerLabel: "SPEAKER_00",
    personId: "p1", personName: "Ada Lovelace", distance: 0.35, speechMs: 42000,
    suggestedAt: "2026-08-25T00:00:00Z", ...over,
  };
}

const segments: AttributionSegment[] = [
  { id: "g1", startMs: 0, endMs: 1000, text: "One" },
  { id: "g2", startMs: 1000, endMs: 2000, text: "Two" },
];

function setup() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <VoicesToConfirmModal onClose={() => {}} />
    </QueryClientProvider>,
  );
}

/// The right-hand panel, so a query for "Ada Lovelace" cannot accidentally match the left-hand row.
const evidence = () => within(screen.getByRole("region", { name: /evidence/i }));

beforeEach(() => {
  vi.clearAllMocks();
  // jsdom implements neither media playback nor object URLs, and the modal legitimately uses both.
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
  window.HTMLMediaElement.prototype.pause = vi.fn();
  URL.createObjectURL = vi.fn(() => "blob:clip");
  URL.revokeObjectURL = vi.fn();
  mock(api.getSpeakerSuggestions).mockResolvedValue([suggestion()]);
  mock(api.getSuggestionSegments).mockResolvedValue(segments);
  mock(api.suggestionClip).mockResolvedValue(new Blob(["RIFF"]));
  mock(api.acceptSpeakerSuggestion).mockResolvedValue(undefined);
  mock(api.rejectSpeakerSuggestion).mockResolvedValue(undefined);
});

describe("VoicesToConfirmModal", () => {
  it("lists a pending voice with enough to place it", async () => {
    setup();

    expect(await screen.findByRole("button", { name: /Ada Lovelace/ })).toBeTruthy();
    expect(screen.getByText(/Standup/)).toBeTruthy();
    // How much they say matters: a near match on four seconds deserves more scepticism than on four minutes.
    expect(screen.getByText(/0:42/)).toBeTruthy();
  });

  it("opens the first voice without being asked", async () => {
    // The queue is worked through, not browsed. Landing on an empty right-hand panel would make every
    // single decision cost an extra click.
    setup();

    await waitFor(() => expect(api.getSuggestionSegments).toHaveBeenCalledWith("sp1"));
    expect(await evidence().findByText("One")).toBeTruthy();
  });

  it("shows the chosen voice's own words, and only those", async () => {
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper", recordingName: "Retro" }),
    ]);
    mock(api.getSuggestionSegments).mockImplementation((id: string) =>
      Promise.resolve(id === "sp2" ? [{ id: "g9", startMs: 0, endMs: 500, text: "Elsewhere" }] : segments),
    );
    setup();

    await userEvent.click(await screen.findByRole("button", { name: /Grace Hopper/ }));

    expect(await evidence().findByText("Elsewhere")).toBeTruthy();
    expect(evidence().queryByText("One")).toBeNull();
  });

  it("plays one segment as a clip of exactly that span", async () => {
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });
    await evidence().findByText("Two");

    await userEvent.click(evidence().getAllByRole("button", { name: /^Play$/ })[1]);

    await waitFor(() => expect(api.suggestionClip).toHaveBeenCalledWith("sp1", 1000, 2000));
  });

  it("turns the playing segment's button into Stop, and only that one", async () => {
    // One button, two states. A separate stop control would sit dead on every row that is not playing.
    //
    // The glyph is asserted as well as the label: they are set independently, so a test that read only the
    // accessible name would pass with the icon frozen as a play triangle - which is what the user is
    // actually looking at when they want to stop it. Mutating the icon alone proved that gap was real.
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });
    await evidence().findByText("Two");

    await userEvent.click(evidence().getAllByRole("button", { name: /^Play$/ })[0]);

    await waitFor(() => expect(evidence().getAllByRole("button", { name: /^Stop$/ })).toHaveLength(1));
    expect(evidence().getAllByRole("button", { name: /^Play$/ })).toHaveLength(1);

    const stopping = evidence().getByRole("button", { name: /^Stop$/ });
    expect(stopping.querySelector("rect")).toBeTruthy();
    expect(stopping.querySelector("polygon")).toBeNull();
    expect(evidence().getByRole("button", { name: /^Play$/ }).querySelector("polygon")).toBeTruthy();
  });

  it("confirms the open voice and drops it from the queue", async () => {
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /^Yes$/ }));

    await waitFor(() => expect(api.acceptSpeakerSuggestion).toHaveBeenCalledWith("sp1"));
    await waitFor(() => expect(screen.queryByRole("button", { name: /Ada Lovelace/ })).toBeNull());
  });

  it("declines the open voice", async () => {
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /^No$/ }));

    await waitFor(() => expect(api.rejectSpeakerSuggestion).toHaveBeenCalledWith("sp1"));
  });

  it("moves on to the next voice once one is decided", async () => {
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper" }),
    ]);
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /^Yes$/ }));

    await waitFor(() => expect(api.getSuggestionSegments).toHaveBeenCalledWith("sp2"));
  });

  it("keeps a voice the server refused to decide", async () => {
    // Dropping it would look like the decision stuck, and it would return on the next load unexplained.
    mock(api.rejectSpeakerSuggestion).mockRejectedValue(new Error("boom"));
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /^No$/ }));

    await waitFor(() => expect(screen.getByText(/Could not save/)).toBeTruthy());
    expect(screen.getByRole("button", { name: /Ada Lovelace/ })).toBeTruthy();
  });

  it("stops the audio when the voice being judged changes", async () => {
    // The segment list is replaced, so without this a clip plays on with nothing on screen able to stop it.
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper" }),
    ]);
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });
    await evidence().findByText("Two");
    await userEvent.click(evidence().getAllByRole("button", { name: /^Play$/ })[0]);
    await waitFor(() => expect(evidence().getAllByRole("button", { name: /^Stop$/ })).toHaveLength(1));

    await userEvent.click(screen.getByRole("button", { name: /Grace Hopper/ }));

    await waitFor(() => expect(evidence().queryByRole("button", { name: /^Stop$/ })).toBeNull());
  });

  it("says so when there is nothing to review", async () => {
    mock(api.getSpeakerSuggestions).mockResolvedValue([]);
    setup();

    expect(await screen.findByText(/Nothing waiting/)).toBeTruthy();
  });
});
