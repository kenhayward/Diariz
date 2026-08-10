import { render, screen } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import RecordHero from "./RecordHero";

const noop = () => {};

// Same concern as AudioSourceChip.test.tsx: the idle pill's "Start recording" text must be gated on the
// capture bar's own width (a container query against CaptureBar's `@container`), not the window's - the
// bar spans `window - left panel - chat panel`, so a `md:` media query keeps the label showing on a wide
// window while the cluster spills over the chat panel. jsdom has no layout engine, so this only proves the
// gating classes are present; the fit itself was verified in the browser.
describe("RecordHero", () => {
  it("gates the idle label on the capture bar's width, not the window's", () => {
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
    expect(label.className).toContain("@xl:inline");
    expect(label.className).not.toContain("md:inline");
  });
});
