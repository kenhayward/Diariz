import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import ScreenshotModal from "./ScreenshotModal";
import type { Screenshot } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: {
    screenshotContentUrl: (r: string, s: string) => `/content/${r}/${s}`,
    screenshotThumbUrl: (r: string, s: string) => `/thumb/${r}/${s}`,
    deleteScreenshot: vi.fn().mockResolvedValue(undefined),
  },
}));

const shots: Screenshot[] = [
  { id: "a", capturedAtMs: 65_000, width: 100, height: 50, sizeBytes: 1, ordinal: 0, createdAt: "" },
  { id: "b", capturedAtMs: 125_000, width: 100, height: 50, sizeBytes: 1, ordinal: 1, createdAt: "" },
];

describe("ScreenshotModal", () => {
  it("shows the selected capture's full image", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    expect(screen.getByRole("img").getAttribute("src")).toBe("/content/r1/a");
  });

  it("includes the capture time in the image's alt text", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    expect(screen.getByRole("img").getAttribute("alt")).toMatch(/1:05/);
  });

  it("moves to the next capture", () => {
    const onIndexChange = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={onIndexChange} onClose={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: /next screenshot/i }));

    expect(onIndexChange).toHaveBeenCalledWith(1);
  });

  it("wraps around from the last capture to the first", () => {
    const onIndexChange = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={1} onIndexChange={onIndexChange} onClose={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: /next screenshot/i }));

    expect(onIndexChange).toHaveBeenCalledWith(0);
  });

  it("wraps around from the first capture to the last with previous", () => {
    const onIndexChange = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={onIndexChange} onClose={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: /previous screenshot/i }));

    expect(onIndexChange).toHaveBeenCalledWith(1);
  });

  it("jumps playback to the moment the capture was taken", () => {
    const onJump = vi.fn();
    render(
      <ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} onJump={onJump} />,
    );

    fireEvent.click(screen.getByRole("button", { name: /jump to 1:05/i }));

    expect(onJump).toHaveBeenCalledWith(65_000);
  });

  it("does not render a jump control when onJump is not given", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    expect(screen.queryByRole("button", { name: /jump to/i })).toBeNull();
  });

  it("does not render a delete control when onDelete is not given", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    expect(screen.queryByRole("button", { name: /delete screenshot/i })).toBeNull();
  });

  it("calls onDelete with the current capture's id", () => {
    const onDelete = vi.fn();
    render(
      <ScreenshotModal
        recordingId="r1"
        shots={shots}
        index={0}
        onIndexChange={() => {}}
        onClose={() => {}}
        onDelete={onDelete}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: /delete screenshot/i }));

    expect(onDelete).toHaveBeenCalledWith("a");
  });

  it("offers a download link to the full-size image", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    const link = screen.getByRole("link", { name: /download screenshot/i });
    expect(link.getAttribute("href")).toBe("/content/r1/a");
  });

  it("closes on Escape", () => {
    const onClose = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={onClose} />);

    fireEvent.keyDown(document, { key: "Escape" });

    expect(onClose).toHaveBeenCalled();
  });

  it("does not close on a backdrop click through to the dialog panel", () => {
    const onClose = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={onClose} />);

    fireEvent.click(screen.getByRole("dialog"));

    expect(onClose).not.toHaveBeenCalled();
  });

  it("closes when the backdrop itself is clicked", () => {
    const onClose = vi.fn();
    const { container } = render(
      <ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={onClose} />,
    );

    fireEvent.click(container.firstChild as Element);

    expect(onClose).toHaveBeenCalled();
  });

  it("closes when the close button is clicked", () => {
    const onClose = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={onClose} />);

    fireEvent.click(screen.getByRole("button", { name: /close screenshot/i }));

    expect(onClose).toHaveBeenCalled();
  });

  it("moves to the next capture on ArrowRight and stops listening after unmount", () => {
    const onIndexChange = vi.fn();
    const { unmount } = render(
      <ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={onIndexChange} onClose={() => {}} />,
    );

    fireEvent.keyDown(document, { key: "ArrowRight" });
    expect(onIndexChange).toHaveBeenCalledWith(1);

    unmount();
    onIndexChange.mockClear();
    fireEvent.keyDown(document, { key: "ArrowRight" });
    expect(onIndexChange).not.toHaveBeenCalled();
  });

  it("moves to the previous capture on ArrowLeft", () => {
    const onIndexChange = vi.fn();
    render(<ScreenshotModal recordingId="r1" shots={shots} index={1} onIndexChange={onIndexChange} onClose={() => {}} />);

    fireEvent.keyDown(document, { key: "ArrowLeft" });

    expect(onIndexChange).toHaveBeenCalledWith(0);
  });

  it("shows the current position within the list", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    expect(screen.getByText("1 of 2")).toBeTruthy();
  });

  it("advances the position label with the index", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={1} onIndexChange={() => {}} onClose={() => {}} />);

    expect(screen.getByText("2 of 2")).toBeTruthy();
  });

  it("starts windowed and toggles full screen on demand", () => {
    render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

    const toggle = screen.getByRole("button", { name: /enter full screen/i });
    expect(toggle.getAttribute("aria-pressed")).toBe("false");

    fireEvent.click(toggle);

    const pressed = screen.getByRole("button", { name: /exit full screen/i });
    expect(pressed.getAttribute("aria-pressed")).toBe("true");

    fireEvent.click(pressed);
    expect(screen.getByRole("button", { name: /enter full screen/i }).getAttribute("aria-pressed")).toBe("false");
  });

  it("renders nothing for an empty list instead of crashing", () => {
    const { container } = render(
      <ScreenshotModal recordingId="r1" shots={[]} index={0} onIndexChange={() => {}} onClose={() => {}} />,
    );

    expect(container.firstChild).toBeNull();
  });

  it("renders nothing for an out-of-range index instead of crashing", () => {
    const { container } = render(
      <ScreenshotModal recordingId="r1" shots={shots} index={5} onIndexChange={() => {}} onClose={() => {}} />,
    );

    expect(container.firstChild).toBeNull();
  });

  describe("zoom and pan", () => {
    it("renders a zoom cluster starting at 100 percent (fit)", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      expect(screen.getByRole("button", { name: /zoom in/i })).toBeTruthy();
      expect(screen.getByRole("button", { name: /zoom out/i })).toBeTruthy();
      expect(screen.getByRole("button", { name: /zoom: 100%/i })).toBeTruthy();
    });

    it("increases zoom when the plus button is clicked", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));

      expect(screen.getByRole("button", { name: /zoom: 125%/i })).toBeTruthy();
      expect(screen.getByRole("img").style.transform).toContain("scale(1.25)");
    });

    it("decreases zoom when the minus button is clicked", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      fireEvent.click(screen.getByRole("button", { name: /zoom out/i }));

      expect(screen.getByRole("button", { name: /zoom: 125%/i })).toBeTruthy();
    });

    it("never zooms out below fit (100%)", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.click(screen.getByRole("button", { name: /zoom out/i }));

      expect(screen.getByRole("button", { name: /zoom: 100%/i })).toBeTruthy();
    });

    it("resets to fit when the zoom percentage is clicked", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      fireEvent.click(screen.getByRole("button", { name: /zoom: 156%/i }));

      expect(screen.getByRole("button", { name: /zoom: 100%/i })).toBeTruthy();
    });

    it("zooms in on the + keyboard shortcut", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.keyDown(document, { key: "+" });

      expect(screen.getByRole("button", { name: /zoom: 125%/i })).toBeTruthy();
    });

    it("zooms in on the = keyboard shortcut", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.keyDown(document, { key: "=" });

      expect(screen.getByRole("button", { name: /zoom: 125%/i })).toBeTruthy();
    });

    it("zooms out on the - keyboard shortcut", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.keyDown(document, { key: "+" });
      fireEvent.keyDown(document, { key: "+" });
      fireEvent.keyDown(document, { key: "-" });

      expect(screen.getByRole("button", { name: /zoom: 125%/i })).toBeTruthy();
    });

    it("resets to fit on the 0 keyboard shortcut", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.keyDown(document, { key: "+" });
      fireEvent.keyDown(document, { key: "0" });

      expect(screen.getByRole("button", { name: /zoom: 100%/i })).toBeTruthy();
    });

    it("does not close or navigate when a zoom shortcut is pressed", () => {
      const onClose = vi.fn();
      const onIndexChange = vi.fn();
      render(
        <ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={onIndexChange} onClose={onClose} />,
      );

      fireEvent.keyDown(document, { key: "+" });
      fireEvent.keyDown(document, { key: "-" });
      fireEvent.keyDown(document, { key: "0" });

      expect(onClose).not.toHaveBeenCalled();
      expect(onIndexChange).not.toHaveBeenCalled();
    });

    it("double-click resets a zoomed-in view back to fit", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      expect(screen.getByRole("button", { name: /zoom: 156%/i })).toBeTruthy();

      fireEvent.doubleClick(screen.getByRole("img"));

      expect(screen.getByRole("button", { name: /zoom: 100%/i })).toBeTruthy();
    });

    it("resets zoom to fit when navigating to a different capture", () => {
      const { rerender } = render(
        <ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />,
      );

      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      expect(screen.getByRole("button", { name: /zoom: 125%/i })).toBeTruthy();

      rerender(<ScreenshotModal recordingId="r1" shots={shots} index={1} onIndexChange={() => {}} onClose={() => {}} />);

      expect(screen.getByRole("button", { name: /zoom: 100%/i })).toBeTruthy();
    });

    it("drags to pan once zoomed in, updating the image's transform", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      const img = screen.getByRole("img") as HTMLImageElement;
      const viewport = img.parentElement as HTMLElement;

      Object.defineProperty(img, "naturalWidth", { value: 4000, configurable: true });
      Object.defineProperty(img, "naturalHeight", { value: 3000, configurable: true });
      viewport.getBoundingClientRect = () =>
        ({ width: 800, height: 600, top: 0, left: 0, right: 800, bottom: 600, x: 0, y: 0, toJSON() {} }) as DOMRect;

      // Two zoom-in steps: 1 * 1.25 * 1.25 = 1.5625, well under the max (native = 1/0.2 = 5x fit here).
      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));

      fireEvent.pointerDown(img, { clientX: 100, clientY: 100, pointerId: 1 });
      fireEvent.pointerMove(img, { clientX: 60, clientY: 70, pointerId: 1 });
      fireEvent.pointerUp(img, { clientX: 60, clientY: 70, pointerId: 1 });

      expect(img.style.transform).toContain("translate(-40px, -30px)");
    });

    it("does not pan while still at fit scale", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      const img = screen.getByRole("img") as HTMLImageElement;
      const viewport = img.parentElement as HTMLElement;
      Object.defineProperty(img, "naturalWidth", { value: 4000, configurable: true });
      Object.defineProperty(img, "naturalHeight", { value: 3000, configurable: true });
      viewport.getBoundingClientRect = () =>
        ({ width: 800, height: 600, top: 0, left: 0, right: 800, bottom: 600, x: 0, y: 0, toJSON() {} }) as DOMRect;

      fireEvent.pointerDown(img, { clientX: 100, clientY: 100, pointerId: 1 });
      fireEvent.pointerMove(img, { clientX: 60, clientY: 70, pointerId: 1 });
      fireEvent.pointerUp(img, { clientX: 60, clientY: 70, pointerId: 1 });

      expect(img.style.transform).not.toContain("translate(-40px, -30px)");
    });

    it("wheel with a negative deltaY zooms in", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      const viewport = screen.getByRole("img").parentElement as HTMLElement;
      fireEvent.wheel(viewport, { deltaY: -200, clientX: 0, clientY: 0 });

      const img = screen.getByRole("img") as HTMLImageElement;
      const match = /scale\(([\d.]+)\)/.exec(img.style.transform);
      expect(match).toBeTruthy();
      expect(Number(match![1])).toBeGreaterThan(1);
    });

    it("wheel with a positive deltaY zooms out (clamped at fit)", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      const viewport = screen.getByRole("img").parentElement as HTMLElement;
      fireEvent.wheel(viewport, { deltaY: 200, clientX: 0, clientY: 0 });

      expect(screen.getByRole("button", { name: /zoom: 100%/i })).toBeTruthy();
    });
  });
});
