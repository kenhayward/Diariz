import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import AudioSourceChip from "./AudioSourceChip";

// The chip renders inside the capture bar, whose column can be much narrower than the window (the column
// is `window - left panel - chat panel`, not tied to a media-query breakpoint). "hidden md:inline" alone
// hides the label below a *window* breakpoint but does nothing when the window is wide and only the column
// is narrow, so the label must also be able to truncate instead of forcing the button's width - see the
// CaptureBar overflow finding. jsdom has no layout engine, so this only proves the truncation classes are
// present, not that the layout actually fits at any given width.
describe("AudioSourceChip", () => {
  it("lets its label truncate rather than force the button's width", () => {
    render(<AudioSourceChip systemAudio={false} expanded={false} onClick={() => {}} />);
    const label = screen.getByText("Audio source");
    expect(label.className).toContain("min-w-0");
    expect(label.className).toContain("truncate");
    // Truncation is additive to the existing narrow-window behaviour, not a replacement for it.
    expect(label.className).toContain("hidden");
    expect(label.className).toContain("md:inline");
  });
});
