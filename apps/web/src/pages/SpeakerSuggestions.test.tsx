import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import SpeakerSuggestions from "./SpeakerSuggestions";
import { api } from "../lib/api";
import type { SpeakerSuggestion } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    getSpeakerSuggestions: vi.fn(),
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

function setup() {
  render(
    <MemoryRouter>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SpeakerSuggestions />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mock(api.getSpeakerSuggestions).mockResolvedValue([suggestion()]);
  mock(api.acceptSpeakerSuggestion).mockResolvedValue(undefined);
  mock(api.rejectSpeakerSuggestion).mockResolvedValue(undefined);
});

describe("SpeakerSuggestions", () => {
  it("lists a pending suggestion with enough to judge it", async () => {
    setup();

    expect(await screen.findByText("Ada Lovelace")).toBeTruthy();
    expect(screen.getByText("Standup")).toBeTruthy();
    expect(screen.getByText("SPEAKER_00")).toBeTruthy();
    // How much they say matters: a near match on four seconds deserves more scepticism than on four minutes.
    expect(screen.getByText("0:42")).toBeTruthy();
  });

  it("links to the recording, where the evidence is", async () => {
    setup();

    const link = (await screen.findByText("Standup")) as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe("/recordings/r1");
  });

  it("says so when there is nothing to review", async () => {
    mock(api.getSpeakerSuggestions).mockResolvedValue([]);
    setup();

    expect(await screen.findByText(/Nothing waiting/)).toBeTruthy();
  });

  it("confirms a suggestion and drops it from the list", async () => {
    setup();
    await screen.findByText("Ada Lovelace");

    await userEvent.click(screen.getByRole("button", { name: /^Yes$/ }));

    await waitFor(() => expect(api.acceptSpeakerSuggestion).toHaveBeenCalledWith("sp1"));
    await waitFor(() => expect(screen.queryByText("Ada Lovelace")).toBeNull());
  });

  it("declines a suggestion", async () => {
    setup();
    await screen.findByText("Ada Lovelace");

    await userEvent.click(screen.getByRole("button", { name: /^No$/ }));

    await waitFor(() => expect(api.rejectSpeakerSuggestion).toHaveBeenCalledWith("sp1"));
  });

  it("keeps a row the server refused to decide", async () => {
    // Dropping it would look like the decision stuck, and it would return on the next load unexplained.
    mock(api.rejectSpeakerSuggestion).mockRejectedValue(new Error("boom"));
    setup();
    await screen.findByText("Ada Lovelace");

    await userEvent.click(screen.getByRole("button", { name: /^No$/ }));

    await waitFor(() => expect(screen.getByText(/Could not save/)).toBeTruthy());
    expect(screen.getByText("Ada Lovelace")).toBeTruthy();
  });

  it("only disables the row being decided", async () => {
    // A slow decision on one voice must not block judging the others.
    let release: (() => void) | undefined;
    mock(api.acceptSpeakerSuggestion).mockReturnValue(new Promise<void>((r) => { release = () => r(); }));
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper" }),
    ]);
    setup();
    await screen.findByText("Ada Lovelace");

    await userEvent.click(screen.getAllByRole("button", { name: /^Yes$/ })[0]);

    const [first, second] = screen.getAllByRole("button", { name: /^Yes$/ }) as HTMLButtonElement[];
    expect(first.disabled).toBe(true);
    expect(second.disabled).toBe(false);
    release?.();
  });
});
