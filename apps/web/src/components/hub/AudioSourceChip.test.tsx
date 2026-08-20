import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import AudioSourceChip from "./AudioSourceChip";

// The chip renders inside the capture bar, whose width is `window - left panel - chat panel` and so is not
// tied to any viewport breakpoint. A `md:` media query measures the wrong box: on a wide window with the
// chat panel dragged out, it keeps the label showing while the cluster spills over the chat panel. The
// label is therefore gated on the bar's own width via a container query (`@xl:`, against CaptureBar's
// `@container`). jsdom has no layout engine, so this only proves the gating classes are present; that the
// cluster actually fits was verified in the browser at a 344px bar (labels drop) and a 952px bar (labels
// return).
describe("AudioSourceChip", () => {
  it("gates its label on the capture bar's width, not the window's", () => {
    render(<AudioSourceChip systemAudio={false} expanded={false} onClick={() => {}} />);
    const label = screen.getByText("Audio source");
    expect(label.className).toContain("min-w-0");
    expect(label.className).toContain("truncate");
    expect(label.className).toContain("hidden");
    expect(label.className).toContain("@xl:inline");
    // The viewport breakpoint is not a fallback: two display rules would fight, and `md:` winning on a wide
    // window is exactly the overflow this replaced.
    expect(label.className).not.toContain("md:inline");
  });

  // A second, tighter step below the label one. With the label already gone the chip is mic + "+System" +
  // chevron, and "+System" alone is 72px of the 144px the chip then occupies - the single biggest thing
  // left to shed. Below a 400px bar the pill becomes a green dot on the chip and the chevron goes, taking
  // the chip to a 44px icon. The threshold is the idle one in both states because the chip is identical
  // whether or not a recording is running. jsdom computes no geometry and loads no Tailwind CSS, so this
  // proves the classes are present and nothing about the layout; the widths were measured in a browser.
  it("sheds the +System pill for a dot, and the chevron, on a narrow bar", () => {
    render(<AudioSourceChip systemAudio expanded={false} onClick={() => {}} />);
    expect(screen.getByText("+System").className).toContain("@max-[400px]:hidden");

    const dot = screen.getByTestId("system-audio-dot");
    expect(dot.className).toContain("hidden");
    expect(dot.className).toContain("@max-[400px]:block");

    expect(screen.getByTestId("source-chevron").className).toContain("@max-[400px]:hidden");
  });

  // The chip looks the same whether or not a recording is running, but the room it has does not: the
  // recording cluster is ~290px wider, so every step it takes has to come earlier. Measured requirements
  // (bar container width): everything on needs 725px while recording against 575px idle, and mic + pill +
  // chevron needs 531px against 343px. Sharing one threshold is what left the recording cluster spilling
  // between 614 and 630px even with the meter already dropped.
  it("takes both of its steps earlier while recording", () => {
    render(<AudioSourceChip recording systemAudio expanded={false} onClick={() => {}} />);

    const label = screen.getByText("Audio source");
    expect(label.className).toContain("@min-[740px]:inline");
    expect(label.className).not.toContain("@xl:inline");

    expect(screen.getByText("+System").className).toContain("@max-[560px]:hidden");
    expect(screen.getByTestId("system-audio-dot").className).toContain("@max-[560px]:block");
    expect(screen.getByTestId("source-chevron").className).toContain("@max-[560px]:hidden");
  });

  // The dot stands in for the pill, so it may only appear when the pill would have.
  it("shows neither pill nor dot when system audio is off", () => {
    render(<AudioSourceChip systemAudio={false} expanded={false} onClick={() => {}} />);
    expect(screen.queryByText("+System")).toBeNull();
    expect(screen.queryByTestId("system-audio-dot")).toBeNull();
  });
});
