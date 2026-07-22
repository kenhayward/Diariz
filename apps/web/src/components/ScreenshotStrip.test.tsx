import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import ScreenshotStrip from "./ScreenshotStrip";
import type { Screenshot } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    screenshotThumbUrl: (r: string, s: string) => `/thumb/${r}/${s}`,
  },
}));

const shots: Screenshot[] = [
  { id: "a", capturedAtMs: 65_000, width: 100, height: 50, sizeBytes: 1, ordinal: 0, createdAt: "" },
  { id: "b", capturedAtMs: 125_000, width: 100, height: 50, sizeBytes: 1, ordinal: 1, createdAt: "" },
];

describe("ScreenshotStrip", () => {
  it("renders a thumbnail per capture with its thumb URL", () => {
    render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={() => {}} />);

    const images = screen.getAllByRole("img");
    expect(images).toHaveLength(2);
    expect(images[0].getAttribute("src")).toBe("/thumb/r1/a");
    expect(images[1].getAttribute("src")).toBe("/thumb/r1/b");
  });

  it("gives each thumbnail alt text that includes its capture time", () => {
    render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={() => {}} />);

    expect(screen.getByAltText(/1:05/)).toBeTruthy();
    expect(screen.getByAltText(/2:05/)).toBeTruthy();
  });

  it("opens the clicked capture at its index", () => {
    const onOpen = vi.fn();
    render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={onOpen} />);

    fireEvent.click(screen.getAllByRole("button")[1]);

    expect(onOpen).toHaveBeenCalledWith(1);
  });

  it("shows an empty state instead of an empty row when there are no captures", () => {
    render(<ScreenshotStrip recordingId="r1" shots={[]} onOpen={() => {}} />);

    expect(screen.queryByRole("img")).toBeNull();
    expect(screen.getByText(/no screenshots captured/i)).toBeTruthy();
  });
});
