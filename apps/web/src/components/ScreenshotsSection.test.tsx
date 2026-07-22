import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import ScreenshotsSection from "./ScreenshotsSection";
import type { Screenshot } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { screenshotThumbUrl: (r: string, s: string) => `/thumb/${r}/${s}` },
}));

const shots: Screenshot[] = [
  { id: "a", capturedAtMs: 1_000, width: 10, height: 10, sizeBytes: 1, ordinal: 0, createdAt: "" },
  { id: "b", capturedAtMs: 2_000, width: 10, height: 10, sizeBytes: 1, ordinal: 1, createdAt: "" },
];

describe("ScreenshotsSection", () => {
  it("is collapsed by default", () => {
    render(<ScreenshotsSection recordingId="r1" shots={shots} onOpen={() => {}} />);

    expect(screen.getByRole("group").getAttribute("open")).toBeNull();
  });

  it("shows the capture count in its label", () => {
    render(<ScreenshotsSection recordingId="r1" shots={shots} onOpen={() => {}} />);

    expect(screen.getByText(/Screenshots \(2\)/)).toBeTruthy();
  });

  it("renders nothing when the recording has no captures", () => {
    const { container } = render(<ScreenshotsSection recordingId="r1" shots={[]} onOpen={() => {}} />);

    expect(container.firstChild).toBeNull();
  });

  it("opens a capture when its thumbnail is clicked", () => {
    const onOpen = vi.fn();
    render(<ScreenshotsSection recordingId="r1" shots={shots} onOpen={onOpen} />);

    // jsdom keeps a closed <details>'s children in the DOM, so the thumbnails are queryable without
    // toggling it open first. The buttons are the thumbnails only - <summary> is not a button.
    fireEvent.click(screen.getAllByRole("button")[0]);

    expect(onOpen).toHaveBeenCalledWith(0);
  });

  it("opens the clicked capture at its own index, not always the first", () => {
    const onOpen = vi.fn();
    render(<ScreenshotsSection recordingId="r1" shots={shots} onOpen={onOpen} />);

    fireEvent.click(screen.getAllByRole("button")[1]);

    expect(onOpen).toHaveBeenCalledWith(1);
  });
});
