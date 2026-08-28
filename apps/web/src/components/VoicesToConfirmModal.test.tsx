import { act, render, screen, waitFor, within } from "@testing-library/react";
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
  { id: "g3", startMs: 2000, endMs: 3000, text: "Three" },
];

/// One reused audio element under the hook's control, so a test can fire `onended` and watch the queue
/// advance. jsdom has no media pipeline, so without this "plays each in turn" could only assert the first
/// clip - which is the half that was already working.
class FakeAudio {
  static instances: FakeAudio[] = [];
  onended: (() => void) | null = null;
  src = "";
  paused = true;
  constructor() {
    FakeAudio.instances.push(this);
  }
  play() {
    this.paused = false;
    return Promise.resolve();
  }
  pause() {
    this.paused = true;
  }
}

const audio = () => FakeAudio.instances[FakeAudio.instances.length - 1];

/// Advance to the next clip the way the browser would, once the current one finishes.
async function finishClip() {
  await act(async () => {
    audio().onended?.();
  });
}

function setup() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <VoicesToConfirmModal onClose={() => {}} />
    </QueryClientProvider>,
  );
}

/// The right-hand panel, so a query for "Ada Lovelace" cannot accidentally match the left-hand row.
const evidence = () => within(screen.getByRole("region", { name: /evidence/i }));

/// The panel only renders once the queue has arrived, so `evidence()` has to be awaited into existence.
async function ready(name = /Ada Lovelace/) {
  await screen.findByRole("button", { name });
  await evidence().findByText("Two");
}

const seg = (text: string) => evidence().getByText(text).closest("li") as HTMLElement;
/// Take one segment out of the list. There is no opposite: a segment is in unless it is removed.
const remove = (text: string) =>
  userEvent.click(within(seg(text)).getByRole("button", { name: /voiceprint/i }));

beforeEach(() => {
  vi.clearAllMocks();
  // jsdom implements neither media playback nor object URLs, and the modal legitimately uses both.
  window.HTMLMediaElement.prototype.play = vi.fn().mockResolvedValue(undefined);
  window.HTMLMediaElement.prototype.pause = vi.fn();
  FakeAudio.instances = [];
  window.Audio = FakeAudio as never;
  // jsdom computes no geometry and implements no scrolling; that the call is made, on the right row, is
  // the whole contract - whether it lands is the browser's job.
  Element.prototype.scrollIntoView = vi.fn();
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
    expect(evidence().getAllByRole("button", { name: /^Play$/ })).toHaveLength(2);

    const stopping = evidence().getByRole("button", { name: /^Stop$/ });
    expect(stopping.querySelector("rect")).toBeTruthy();
    expect(stopping.querySelector("polygon")).toBeNull();
    expect(
      evidence().getAllByRole("button", { name: /^Play$/ })[0].querySelector("polygon"),
    ).toBeTruthy();
  });

  it("confirms the open voice and drops it from the queue", async () => {
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /Confirm this voice/ }));

    await waitFor(() => expect(api.acceptSpeakerSuggestion).toHaveBeenCalledWith("sp1", undefined));
    await waitFor(() => expect(screen.queryByRole("button", { name: /Ada Lovelace/ })).toBeNull());
  });

  it("declines the open voice", async () => {
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /Not this person/ }));

    await waitFor(() => expect(api.rejectSpeakerSuggestion).toHaveBeenCalledWith("sp1"));
  });

  it("moves on to the next voice once one is decided", async () => {
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper" }),
    ]);
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /Confirm this voice/ }));

    await waitFor(() => expect(api.getSuggestionSegments).toHaveBeenCalledWith("sp2"));
  });

  it("keeps a voice the server refused to decide", async () => {
    // Dropping it would look like the decision stuck, and it would return on the next load unexplained.
    mock(api.rejectSpeakerSuggestion).mockRejectedValue(new Error("boom"));
    setup();
    await screen.findByRole("button", { name: /Ada Lovelace/ });

    await userEvent.click(evidence().getByRole("button", { name: /Not this person/ }));

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

  // ---- Judging a speaker one segment at a time ----
  //
  // A diarization label is not always one human. Answering only for the whole list forces a reviewer who
  // can hear that part of it is somebody else either to accept audio that is not this person, or to throw
  // away a correct identification. Excluding a segment therefore has to reach the voiceprint - otherwise
  // the control is a lie, and the enrolment takes in exactly the audio just marked as not them.

  it("takes a segment out of the list when you say it is not them", async () => {
    setup();
    await ready();

    await remove("Two");

    expect(evidence().queryByText("Two")).toBeNull();
    expect(evidence().getByText("One")).toBeTruthy();
    expect(evidence().getByText("Three")).toBeTruthy();
  });

  it("trains the voiceprint from only the segments that were kept", async () => {
    setup();
    await ready();
    await remove("Two");

    await userEvent.click(evidence().getByRole("button", { name: /Confirm this voice/ }));

    await waitFor(() =>
      expect(api.acceptSpeakerSuggestion).toHaveBeenCalledWith("sp1", [
        { startMs: 0, endMs: 1000 },
        { startMs: 2000, endMs: 3000 },
      ]),
    );
  });

  it("asks for the whole speaker when nothing was excluded", async () => {
    // The overwhelmingly common case, and it stays the cheap one: no spans means the whole speaker, and
    // sending the full list instead would queue a re-embed that changes nothing.
    setup();
    await ready();

    await userEvent.click(evidence().getByRole("button", { name: /Confirm this voice/ }));

    await waitFor(() => expect(api.acceptSpeakerSuggestion).toHaveBeenCalledWith("sp1", undefined));
  });

  it("puts back segments excluded by mistake", async () => {
    // One click, no undo, and it shapes a biometric. Reopening the modal is not a recovery path.
    setup();
    await ready();
    await remove("Two");

    await userEvent.click(evidence().getByRole("button", { name: /Restore/ }));

    expect(await evidence().findByText("Two")).toBeTruthy();
  });

  it("offers no per-segment tick, because there was nothing for it to do", async () => {
    // It rendered pressed and filled when clicked, next to a same-sized cross, so it read as choosing what
    // would be used. It changed nothing: a segment trains unless it is removed, and a ticked segment and an
    // untouched one were identical. Two controls that look like opposites, one of them inert.
    setup();
    await ready();

    const row = within(seg("Two"));
    expect(row.getByRole("button", { name: /voiceprint/i })).toBeTruthy();
    expect(row.queryByRole("button", { name: /^Yes$/ })).toBeNull();
  });

  it("says what confirming will train from, before anything is excluded", async () => {
    // The count used to appear only once something had been removed, so the commonest case - look, decide,
    // confirm - was the one the panel said nothing about. That silence is what made the tick misleading.
    setup();
    await ready();

    expect(evidence().getByText(/3 of 3 segments/)).toBeTruthy();
  });

  it("does not carry one voice's marks to another", async () => {
    // Exclusions belong to the voice being judged. Leaking them across would silently drop audio from a
    // different person's voiceprint.
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper" }),
    ]);
    setup();
    await ready();
    await remove("Two");

    await userEvent.click(screen.getByRole("button", { name: /Grace Hopper/ }));

    expect(await evidence().findByText("Two")).toBeTruthy();
  });

  it("keeps each voice's marks when you look at another and come back", async () => {
    // Reported from live use: marking segments and then glancing at another voice threw the work away
    // silently. Keeping the marks per voice gives the isolation above without losing what was decided -
    // the two are not in tension, and the first version confused them.
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper" }),
    ]);
    setup();
    await ready();
    await remove("Two");

    await userEvent.click(screen.getByRole("button", { name: /Grace Hopper/ }));
    await evidence().findByText("Two");
    await userEvent.click(screen.getByRole("button", { name: /Ada Lovelace/ }));
    await evidence().findByText("One");

    expect(evidence().queryByText("Two")).toBeNull();
    expect(evidence().getByText(/2 of 3 segments/)).toBeTruthy();
  });

  it("still trains from the kept segments after switching away and back", async () => {
    // The marks surviving is only worth anything if they are still what gets sent.
    mock(api.getSpeakerSuggestions).mockResolvedValue([
      suggestion(),
      suggestion({ speakerId: "sp2", personName: "Grace Hopper" }),
    ]);
    setup();
    await ready();
    await remove("Two");
    await userEvent.click(screen.getByRole("button", { name: /Grace Hopper/ }));
    await evidence().findByText("Two");
    await userEvent.click(screen.getByRole("button", { name: /Ada Lovelace/ }));
    await evidence().findByText("One");

    await userEvent.click(evidence().getByRole("button", { name: /Confirm this voice/ }));

    await waitFor(() =>
      expect(api.acceptSpeakerSuggestion).toHaveBeenCalledWith("sp1", [
        { startMs: 0, endMs: 1000 },
        { startMs: 2000, endMs: 3000 },
      ]),
    );
  });

  it("says in words what the confirm button will do", async () => {
    // Asked in live use: "how do I make this permanent?". The answer was already the tick at the top, but
    // an icon with only a tooltip did not say so, and there is no separate save to look for.
    setup();
    await ready();

    const confirm = evidence().getByRole("button", { name: /Confirm this voice/ });
    expect(confirm.textContent).toMatch(/Confirm this voice/);
  });

  it("says how much of the voice will train the voiceprint", async () => {
    // The consequence of the exclusions, stated before they are committed rather than after.
    setup();
    await ready();

    await remove("Two");

    expect(await evidence().findByText(/2 of 3 segments/)).toBeTruthy();
  });

  it("puts the play button beside the answer, not across the row from it", async () => {
    // Working through a queue is listen-then-decide, over and over. With the play control at the far left
    // and the answer at the far right, every segment costs a full traverse of the panel.
    setup();
    await ready();

    const parts = [...seg("One").children];
    const at = (label: string) =>
      parts.findIndex((el) =>
        (el.getAttribute("aria-label") ?? "").toLowerCase().includes(label.toLowerCase()),
      );

    expect(at("voiceprint")).toBe(at("Play") + 1);
    // The words still come first, so the three controls sit together at the end of the row.
    expect(parts.findIndex((el) => el.textContent === "One")).toBeLessThan(at("Play"));
  });

  // ---- Saying what a removal actually does ----
  //
  // Taking a segment out shapes a biometric: the segments left in are sent as spans on confirm and the
  // worker re-embeds from exactly those. A user asked outright whether it did anything to voiceprints,
  // having concluded from the panel that it did not - every control around it described the list rather
  // than the consequence.

  it("says on the control itself that a removed segment is left out of the voiceprint", async () => {
    setup();
    await ready();

    const control = within(seg("Two")).getByRole("button", { name: /voiceprint/i });

    expect(control.getAttribute("title")).toMatch(/voiceprint/i);
  });

  it("says what the removed segments are excluded from", async () => {
    setup();
    await ready();

    await remove("Two");

    // Not a bare "1 segment excluded", which never said excluded from what.
    expect(evidence().getByText(/1 segment .*voiceprint/i)).toBeTruthy();
  });

  it("tells you up front that confirming trains from the segments you leave in", async () => {
    // The intro said "Confirming teaches the voiceprint" and nothing about segments, so it read as though
    // the whole recording was used whatever you did to the list.
    setup();
    await ready();

    expect(screen.getByText(/segments you leave in/i)).toBeTruthy();
  });

  // ---- Playing the whole voice through ----

  it("plays every kept segment in turn", async () => {
    setup();
    await ready();

    await userEvent.click(evidence().getByRole("button", { name: /Play all/ }));

    await waitFor(() => expect(api.suggestionClip).toHaveBeenCalledWith("sp1", 0, 1000));
    await finishClip();
    await waitFor(() => expect(api.suggestionClip).toHaveBeenCalledWith("sp1", 1000, 2000));
    await finishClip();
    await waitFor(() => expect(api.suggestionClip).toHaveBeenCalledWith("sp1", 2000, 3000));
  });

  it("skips a segment that was excluded", async () => {
    setup();
    await ready();
    await remove("Two");

    await userEvent.click(evidence().getByRole("button", { name: /Play all/ }));

    await waitFor(() => expect(api.suggestionClip).toHaveBeenCalledWith("sp1", 0, 1000));
    await finishClip();
    await waitFor(() => expect(api.suggestionClip).toHaveBeenCalledWith("sp1", 2000, 3000));
    expect(api.suggestionClip).not.toHaveBeenCalledWith("sp1", 1000, 2000);
  });

  it("brings the segment being played into view", async () => {
    // A long list scrolls, so the highlight is invisible for most of a play-through without this - which is
    // exactly when following along is the point.
    setup();
    await ready();

    await userEvent.click(evidence().getByRole("button", { name: /Play all/ }));
    await waitFor(() => expect(api.suggestionClip).toHaveBeenCalledWith("sp1", 0, 1000));
    await finishClip();

    await waitFor(() => {
      const rows = mock(Element.prototype.scrollIntoView).mock.instances as HTMLElement[];
      expect(rows[rows.length - 1]).toBe(seg("Two"));
    });
  });

  it("turns Play all into Stop while it is running", async () => {
    setup();
    await ready();

    await userEvent.click(evidence().getByRole("button", { name: /Play all/ }));

    await waitFor(() => expect(evidence().getByRole("button", { name: /Stop all/ })).toBeTruthy());
    expect(evidence().queryByRole("button", { name: /Play all/ })).toBeNull();

    // The glyph as well as the words: they are set independently, so a test reading only the label passes
    // with the icon frozen as a play triangle - which is what the user is looking at when they want to
    // stop it. Mutating the icon alone proved that gap was real, here and on the per-segment button.
    const stopping = evidence().getByRole("button", { name: /Stop all/ });
    expect(stopping.querySelector("rect")).toBeTruthy();
    expect(stopping.querySelector("polygon")).toBeNull();
  });
});
