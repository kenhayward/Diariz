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

  // ---- Dragging a capture into the chat prompt ----

  /// jsdom has no DataTransfer, so the drop payload is captured through a stub. That is the whole
  /// contract between the strip and the chat composer, so it is asserted literally.
  function dragThumbnail(index: number) {
    const setData = vi.fn();
    fireEvent.dragStart(screen.getAllByRole("button")[index], { dataTransfer: { setData, effectAllowed: "" } });
    return setData;
  }

  it("marks thumbnails draggable only when the caller asks for it", () => {
    const { unmount } = render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={() => {}} />);
    expect(screen.getAllByRole("button")[0].getAttribute("draggable")).toBe("false");
    unmount();

    render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={() => {}} draggable />);
    expect(screen.getAllByRole("button")[0].getAttribute("draggable")).toBe("true");
  });

  it("writes the capture reference under its own MIME type on drag", () => {
    render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={() => {}} draggable />);

    const setData = dragThumbnail(1);

    expect(setData).toHaveBeenCalledWith(
      "application/x-diariz-screenshot",
      JSON.stringify({ recordingId: "r1", screenshotId: "b", capturedAtMs: 125_000 }),
    );
  });

  it("does not write a payload when dragging is not enabled", () => {
    render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={() => {}} />);

    expect(dragThumbnail(0)).not.toHaveBeenCalled();
  });

  it("still opens a capture on click when dragging is enabled", () => {
    const onOpen = vi.fn();
    render(<ScreenshotStrip recordingId="r1" shots={shots} onOpen={onOpen} draggable />);

    fireEvent.click(screen.getAllByRole("button")[1]);

    expect(onOpen).toHaveBeenCalledWith(1);
  });
});
