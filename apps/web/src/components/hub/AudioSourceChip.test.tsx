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
});
