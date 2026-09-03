import { render, screen, fireEvent, act } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import LiveNotesStream, { type LiveNotesStreamProps } from "./LiveNotesStream";
import type { LiveSegment, LiveTranscript } from "../../lib/liveTranscript";
import type { MeetingNote, ShotView } from "../../lib/types";

const note = (over: Partial<MeetingNote> & { capturedAtMs: number | null }): MeetingNote => ({
  id: `n-${over.capturedAtMs}`,
  text: "a thought",
  ordinal: 0,
  createdAt: "2026-09-03T10:00:00.000Z",
  ...over,
});

let nextShot = 0;
const shot = (capturedAtMs: number): ShotView => ({
  id: `c-${nextShot++}`,
  capturedAtMs,
  thumb: new Blob(["t"], { type: "image/jpeg" }),
});

const transcript = (...segments: (Partial<LiveSegment> & { startMs: number })[]): LiveTranscript => ({
  recordingId: "rec-1",
  highestSequence: 0,
  segments: segments.map((s) => ({
    id: `s-${s.startMs}`,
    endMs: s.startMs + 3000,
    text: "said something",
    sequence: 0,
    ...s,
  })),
});

const base: LiveNotesStreamProps = {
  lines: [],
  shots: [],
  elapsedMs: 0,
  onAdd: () => {},
  onEdit: () => {},
  onDelete: () => {},
  onDeleteShot: () => {},
  variant: "popover",
};

// The desktop shell's half. Both handlers arrive together, so a test wanting captures supplies the pair.
const capture = { captureAreaSet: true, onCapture: () => {}, onChangeArea: () => {} };

function renderStream(over: Partial<LiveNotesStreamProps> = {}) {
  return render(<LiveNotesStream {...base} {...over} />);
}

const composer = () => screen.getByLabelText(/note this moment/i) as HTMLInputElement;

describe("LiveNotesStream", () => {
  it("puts notes, captures and transcript lines on one list in timeline order", () => {
    // The whole point of the redesign: no tabs, one stream, everything stamped where it happened.
    renderStream({
      lines: [note({ capturedAtMs: 20_000, text: "my point" })],
      shots: [shot(5_000)],
      liveTranscript: transcript({ startMs: 10_000, text: "what was said" }),
      capture,
    });

    const kinds = Array.from(
      screen.getByTestId("notes-stream").querySelectorAll("li"),
      (li) => li.getAttribute("data-testid"),
    );
    expect(kinds).toEqual(["stream-capture", "stream-transcript", "stream-note"]);
  });

  it("offers no tabs at all", () => {
    renderStream({ liveTranscript: transcript({ startMs: 0 }) });

    expect(screen.queryAllByRole("tab")).toHaveLength(0);
  });
});

describe("LiveNotesStream filter chips", () => {
  const populated = {
    lines: [note({ capturedAtMs: 1_000 }), note({ capturedAtMs: 2_000, id: "n2" })],
    shots: [shot(3_000)],
    liveTranscript: transcript({ startMs: 4_000 }),
    capture,
  };

  it("counts the whole meeting on the chips", () => {
    renderStream(populated);

    expect(screen.getByRole("radio", { name: /notes 2/i })).toBeTruthy();
    expect(screen.getByRole("radio", { name: /captures 1/i })).toBeTruthy();
  });

  it("narrows the stream to notes, and keeps the counts showing the whole meeting", () => {
    renderStream(populated);

    fireEvent.click(screen.getByRole("radio", { name: /notes 2/i }));

    expect(screen.queryAllByTestId("stream-transcript")).toHaveLength(0);
    expect(screen.queryAllByTestId("stream-capture")).toHaveLength(0);
    expect(screen.getAllByTestId("stream-note")).toHaveLength(2);
    // Still 1, not 0: a count that emptied when its own chip was deselected would be telling the user
    // their captures had gone.
    expect(screen.getByRole("radio", { name: /captures 1/i })).toBeTruthy();
  });

  it("narrows the stream to captures", () => {
    renderStream(populated);

    fireEvent.click(screen.getByRole("radio", { name: /captures 1/i }));

    expect(screen.getAllByTestId("stream-capture")).toHaveLength(1);
    expect(screen.queryAllByTestId("stream-note")).toHaveLength(0);
  });

  it("marks which chip is chosen", () => {
    renderStream(populated);

    expect(screen.getByRole("radio", { name: /everything/i }).getAttribute("aria-checked")).toBe("true");
    fireEvent.click(screen.getByRole("radio", { name: /notes 2/i }));
    expect(screen.getByRole("radio", { name: /everything/i }).getAttribute("aria-checked")).toBe("false");
    expect(screen.getByRole("radio", { name: /notes 2/i }).getAttribute("aria-checked")).toBe("true");
  });

  it("offers no Captures chip where nothing can ever be captured", () => {
    // A plain browser has no capture bridge, so the chip would filter to a permanently empty list.
    renderStream({ ...populated, capture: undefined });

    expect(screen.getByRole("radio", { name: /notes 2/i })).toBeTruthy();
    expect(screen.queryByRole("radio", { name: /captures/i })).toBeNull();
  });
});

describe("LiveNotesStream composer", () => {
  it("files a note at the running clock and clears the box", () => {
    const onAdd = vi.fn();
    renderStream({ elapsedMs: 61_000, onAdd });

    fireEvent.change(composer(), { target: { value: "a thought" } });
    fireEvent.keyDown(composer(), { key: "Enter" });

    // No stamp: the host reads its own pause-aware clock, which this panel cannot.
    expect(onAdd).toHaveBeenCalledWith("a thought", undefined);
    expect(composer().value).toBe("");
  });

  it("shows the running clock on the stamp badge", () => {
    renderStream({ elapsedMs: 61_000 });

    expect(screen.getByTestId("composer-stamp").textContent).toBe("1:01");
  });

  it("does nothing on Enter with only whitespace typed", () => {
    const onAdd = vi.fn();
    renderStream({ onAdd });

    fireEvent.change(composer(), { target: { value: "   " } });
    fireEvent.keyDown(composer(), { key: "Enter" });

    expect(onAdd).not.toHaveBeenCalled();
  });

  it("pins the badge to a transcript line and puts the cursor in the box", () => {
    // Someone hears a sentence and wants to write about it forty seconds later; the note belongs
    // beside what it is about, not where the clock happens to be.
    renderStream({ elapsedMs: 61_000, liveTranscript: transcript({ startMs: 20_000 }) });

    fireEvent.click(screen.getByRole("button", { name: /write a note about this moment/i }));

    expect(screen.getByTestId("composer-stamp").textContent).toBe("0:20");
    expect(document.activeElement).toBe(composer());
  });

  it("files at the pinned moment, then follows the clock again", () => {
    const onAdd = vi.fn();
    renderStream({ elapsedMs: 61_000, onAdd, liveTranscript: transcript({ startMs: 20_000 }) });

    fireEvent.click(screen.getByRole("button", { name: /write a note about this moment/i }));
    fireEvent.change(composer(), { target: { value: "about that" } });
    fireEvent.keyDown(composer(), { key: "Enter" });

    expect(onAdd).toHaveBeenCalledWith("about that", 20_000);
    // A pin left set would file the NEXT note - about whatever is being said now - back at 0:20, which
    // is the one mistake this control can make without showing anything.
    expect(screen.getByTestId("composer-stamp").textContent).toBe("1:01");
  });

  it("releases a pin without filing anything", () => {
    // Escape cannot do this: HubPopover closes the whole panel on Escape. So the badge itself is the
    // way back to the clock.
    const onAdd = vi.fn();
    renderStream({ elapsedMs: 61_000, onAdd, liveTranscript: transcript({ startMs: 20_000 }) });
    fireEvent.click(screen.getByRole("button", { name: /write a note about this moment/i }));

    fireEvent.click(screen.getByRole("button", { name: /follow the clock again/i }));

    expect(onAdd).not.toHaveBeenCalled();
    expect(screen.getByTestId("composer-stamp").textContent).toBe("1:01");
  });

  it("goes inert but stays visible when the host is unreachable", () => {
    // A vanished box would read as the notes themselves having gone; a dead one reads as paused, which
    // is what has actually happened.
    const onAdd = vi.fn();
    renderStream({ disabled: true, onAdd });

    expect(composer().disabled).toBe(true);
    fireEvent.change(composer(), { target: { value: "into the void" } });
    fireEvent.keyDown(composer(), { key: "Enter" });
    expect(onAdd).not.toHaveBeenCalled();
  });
});

describe("LiveNotesStream note rows", () => {
  it("routes an edit out rather than applying it locally", () => {
    const onEdit = vi.fn();
    renderStream({ lines: [note({ capturedAtMs: 1_000, id: "n1", text: "First point" })], onEdit });

    fireEvent.click(screen.getByRole("button", { name: /edit note/i }));
    fireEvent.change(screen.getByLabelText(/edit note/i), { target: { value: "Revised" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    expect(onEdit).toHaveBeenCalledWith("n1", "Revised");
  });

  it("abandons an edit on cancel", () => {
    const onEdit = vi.fn();
    renderStream({ lines: [note({ capturedAtMs: 1_000, id: "n1", text: "First point" })], onEdit });

    fireEvent.click(screen.getByRole("button", { name: /edit note/i }));
    fireEvent.change(screen.getByLabelText(/edit note/i), { target: { value: "Revised" } });
    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));

    expect(onEdit).not.toHaveBeenCalled();
    expect(screen.getByText("First point")).toBeTruthy();
  });

  it("routes a delete out, naming the line rather than its position", () => {
    const onDelete = vi.fn();
    renderStream({
      lines: [note({ capturedAtMs: 1_000, id: "n1" }), note({ capturedAtMs: 2_000, id: "n2" })],
      onDelete,
    });

    fireEvent.click(screen.getAllByRole("button", { name: /delete note/i })[1]);

    expect(onDelete).toHaveBeenCalledWith("n2");
  });

  it("stamps a note adopted from a pre-meeting stash at the start rather than dropping it", () => {
    renderStream({ lines: [note({ capturedAtMs: null, text: "written beforehand" })] });

    expect(screen.getByText("written beforehand")).toBeTruthy();
  });
});

describe("LiveNotesStream transcript rows", () => {
  it("shows who is speaking", () => {
    renderStream({
      liveTranscript: transcript(
        { startMs: 0, speaker: "Ada" },
        { startMs: 4_000, speaker: "Grace" },
      ),
    });

    expect(screen.getAllByTestId("stream-speaker").map((n) => n.textContent)).toEqual(["Ada", "Grace"]);
  });

  it("repeats a speaker's name only when it changes", () => {
    renderStream({
      liveTranscript: transcript(
        { startMs: 0, speaker: "Ada" },
        { startMs: 4_000, speaker: "Ada" },
        { startMs: 8_000, speaker: "Grace" },
      ),
    });

    expect(screen.getAllByTestId("stream-speaker").map((n) => n.textContent)).toEqual(["Ada", "Grace"]);
  });

  it("marks a suggested name as a guess rather than stating it", () => {
    renderStream({
      liveTranscript: transcript(
        { startMs: 0, speaker: "Ada" },
        { startMs: 4_000, speaker: "Grace", speakerIsSuggestion: true },
      ),
    });

    const [confident, guess] = screen.getAllByTestId("stream-speaker");
    expect(guess.getAttribute("data-suggestion")).toBe("true");
    expect(guess.textContent).toBe("Grace?");
    expect(confident.getAttribute("data-suggestion")).not.toBe("true");
  });

  it("renders a line with no speaker at all", () => {
    renderStream({ liveTranscript: transcript({ startMs: 0, text: "anonymous" }) });

    expect(screen.getByText("anonymous")).toBeTruthy();
    expect(screen.queryByTestId("stream-speaker")).toBeNull();
  });

  it("stamps every line with the moment it was said", () => {
    // The per-line timestamp is what the redesign adds; without it a note pinned to a line has nothing
    // to point at.
    renderStream({ liveTranscript: transcript({ startMs: 65_000 }) });

    expect(screen.getByTestId("stream-transcript").textContent).toContain("1:05");
  });
});

describe("LiveNotesStream status line", () => {
  it("says the transcript is keeping up", () => {
    renderStream({ liveTranscript: transcript({ startMs: 0 }), liveLagSeconds: 0 });

    expect(screen.getByTestId("live-transcript-status").textContent).toMatch(/live/i);
  });

  it("reports how far behind it is, and keeps the full caveat on hover", () => {
    // The line is 11px and shares its row with a confirmation, so the sentence saying the text is not
    // final cannot fit in it - but it must not be lost either.
    renderStream({ liveTranscript: transcript({ startMs: 0 }), liveLagSeconds: 16 });

    const status = screen.getByTestId("live-transcript-status");
    expect(status.textContent).toMatch(/16/);
    expect(status.getAttribute("title")).toMatch(/not final/i);
  });

  it("explains a paused transcript without raising an alert", () => {
    // Falling behind costs the running commentary and nothing else. Announcing it as an error would
    // interrupt the user to tell them about something that fixes itself.
    renderStream({ liveTranscript: transcript({ startMs: 0 }), liveDegraded: true });

    expect(screen.getByTestId("live-transcript-status").textContent).toMatch(/paused/i);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("shows no status line at all when nothing is being transcribed live", () => {
    // An older server, or a capture that began before the server could be reached. A green "Live" dot
    // over a transcript that is never coming would be a lie.
    renderStream({ lines: [note({ capturedAtMs: 1_000 })] });

    expect(screen.queryByTestId("live-transcript-status")).toBeNull();
  });
});

describe("LiveNotesStream empty states", () => {
  it("says the transcript has not started yet", () => {
    renderStream({ liveTranscript: transcript() });

    expect(screen.getByTestId("notes-stream-empty").textContent).toMatch(/waiting for the first transcript/i);
  });

  it("says there are no notes yet where no transcript is running", () => {
    renderStream();

    expect(screen.getByTestId("notes-stream-empty").textContent).toMatch(/no notes yet/i);
  });

  it("says there are no captures under the Captures filter", () => {
    renderStream({ lines: [note({ capturedAtMs: 1_000 })], capture });

    fireEvent.click(screen.getByRole("radio", { name: /captures 0/i }));

    expect(screen.getByTestId("notes-stream-empty").textContent).toMatch(/no screenshots/i);
  });
});

describe("LiveNotesStream captures", () => {
  let urlCounter = 0;
  let createSpy: ReturnType<typeof vi.spyOn>;
  let revokeSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    urlCounter = 0;
    createSpy = vi.spyOn(URL, "createObjectURL").mockImplementation(() => `blob:mock-${urlCounter++}`);
    revokeSpy = vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
  });
  afterEach(() => {
    createSpy.mockRestore();
    revokeSpy.mockRestore();
  });

  it("makes exactly one object URL per capture", () => {
    renderStream({ shots: [shot(1_000), shot(2_000)], capture });

    expect(createSpy).toHaveBeenCalledTimes(2);
    expect(screen.getAllByRole("img")).toHaveLength(2);
  });

  it("revokes the previous URLs when the capture set changes", () => {
    // Without this a long meeting leaks one blob URL per capture taken.
    const first = [shot(1_000)];
    const { rerender } = renderStream({ shots: first, capture });

    rerender(<LiveNotesStream {...base} capture={capture} shots={[...first, shot(2_000)]} />);

    expect(revokeSpy).toHaveBeenCalledWith("blob:mock-0");
  });

  it("revokes object URLs on unmount", () => {
    const { unmount } = renderStream({ shots: [shot(1_000), shot(2_000)], capture });

    unmount();

    expect(revokeSpy).toHaveBeenCalledWith("blob:mock-0");
    expect(revokeSpy).toHaveBeenCalledWith("blob:mock-1");
  });

  it("gives each thumbnail alt text naming the moment it was taken", () => {
    renderStream({ shots: [shot(3_904_000)], capture });

    expect(screen.getByRole("img").getAttribute("alt")).toContain("1:05:04");
  });

  it("deletes the capture under the clicked button, naming it rather than its position", () => {
    const onDeleteShot = vi.fn();
    const shots = [shot(1_000), shot(2_000), shot(3_000)];
    renderStream({ shots, onDeleteShot, capture });

    fireEvent.click(screen.getAllByRole("button", { name: /delete screenshot/i })[1]);

    expect(onDeleteShot).toHaveBeenCalledWith(shots[1].id);
  });

  it("shows no capture controls where the host cannot capture", () => {
    renderStream({ capture: undefined });

    expect(screen.queryByRole("button", { name: /capture screenshot/i })).toBeNull();
  });

  it("passes the capture-area gate down", () => {
    const onCapture = vi.fn();
    renderStream({ capture: { ...capture, captureAreaSet: false, onCapture } });

    fireEvent.click(screen.getByRole("button", { name: /capture screenshot/i }));

    expect(onCapture).not.toHaveBeenCalled();
  });
});

describe("LiveNotesStream stamp column", () => {
  it("widens once the meeting passes an hour, so nothing wraps", () => {
    const { rerender } = renderStream({
      elapsedMs: 60_000,
      liveTranscript: transcript({ startMs: 0 }),
    });
    const width = () => (screen.getByTestId("stream-transcript").firstElementChild as HTMLElement).style.width;
    expect(width()).toBe("34px");

    rerender(
      <LiveNotesStream {...base} elapsedMs={3_700_000} liveTranscript={transcript({ startMs: 0 })} />,
    );

    expect(width()).toBe("50px");
  });
});

describe("LiveNotesStream auto-scroll", () => {
  /// jsdom lays nothing out, so the three numbers the scroll rule reads are stubbed. `scrollTop` is a
  /// real writable property; the other two are getters on the prototype.
  function fakeScrollBox(el: HTMLElement, { scrollHeight, clientHeight }: { scrollHeight: number; clientHeight: number }) {
    Object.defineProperty(el, "scrollHeight", { configurable: true, get: () => scrollHeight });
    Object.defineProperty(el, "clientHeight", { configurable: true, get: () => clientHeight });
  }

  it("follows the meeting while the list is at the tail", () => {
    const { rerender } = renderStream({ liveTranscript: transcript({ startMs: 0 }) });
    const list = screen.getByTestId("notes-stream");
    fakeScrollBox(list, { scrollHeight: 900, clientHeight: 300 });

    act(() => {
      rerender(
        <LiveNotesStream
          {...base}
          liveTranscript={transcript({ startMs: 0 }, { startMs: 4_000 })}
        />,
      );
    });

    expect(list.scrollTop).toBe(900);
  });

  it("leaves a list the user has scrolled up exactly where they put it", () => {
    // Someone re-reading something said five minutes ago must not be yanked back to the bottom every
    // time the meeting produces another line.
    const { rerender } = renderStream({ liveTranscript: transcript({ startMs: 0 }) });
    const list = screen.getByTestId("notes-stream");
    fakeScrollBox(list, { scrollHeight: 900, clientHeight: 300 });
    list.scrollTop = 100;
    fireEvent.scroll(list);

    act(() => {
      rerender(
        <LiveNotesStream
          {...base}
          liveTranscript={transcript({ startMs: 0 }, { startMs: 4_000 })}
        />,
      );
    });

    expect(list.scrollTop).toBe(100);
  });
});
