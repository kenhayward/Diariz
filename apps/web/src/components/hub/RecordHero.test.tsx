import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import RecordHero from "./RecordHero";

const noop = () => {};

// Same shrink concern as AudioSourceChip.test.tsx: the idle pill's "Start recording" text is only
// conditionally hidden by *window* width (hidden md:inline), which does not help when the window is wide
// but the capture bar's column is narrow - so the label must be able to truncate instead of forcing the
// pill's width. jsdom has no layout engine, so this only proves the truncation classes are present.
describe("RecordHero", () => {
  it("lets the idle label truncate rather than force the pill's width", () => {
    render(
      <RecordHero
        recording={false}
        paused={false}
        mmss="00:00"
        stream={null}
        canRecord
        busy={false}
        startDisabled={false}
        onStart={noop}
        onPause={noop}
        onResume={noop}
        onStop={noop}
        onSilentChange={noop}
      />,
    );
    const label = screen.getByText("Start recording");
    expect(label.className).toContain("min-w-0");
    expect(label.className).toContain("truncate");
    expect(label.className).toContain("hidden");
    expect(label.className).toContain("md:inline");
  });
});
