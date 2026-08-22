import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ScreenshotModal from "./ScreenshotModal";
import type { Screenshot } from "../lib/types";
import { onChatScreenshotAttached, onChatTextAttached } from "../lib/chatAttachments";
import { api } from "../lib/api";

vi.mock("../lib/api", () => ({
  api: {
    screenshotContentUrl: (r: string, s: string) => `/content/${r}/${s}`,
    screenshotThumbUrl: (r: string, s: string) => `/thumb/${r}/${s}`,
    deleteScreenshot: vi.fn().mockResolvedValue(undefined),
    ocrScreenshot: vi.fn().mockResolvedValue({
      text: "Extracted text",
      model: "olmocr-2-7b-1025",
      chars: 14,
      cached: false,
      generatedAt: "2026-08-22T12:00:00Z",
    }),
    addMarkdownAttachment: vi.fn().mockResolvedValue({ id: "att-1" }),
  },
}));

/// The default OCR reply, restored before every test.
///
/// Without this the `mockResolvedValueOnce` / `mockRejectedValueOnce` queues leak across tests: a value
/// queued by one test is consumed by whichever call happens next, which made a test that passes alone fail
/// in the suite. Resetting is cheaper to reason about than ordering the tests around each other.
const OCR_REPLY = {
  text: "Extracted text",
  model: "olmocr-2-7b-1025",
  chars: 14,
  cached: false,
  generatedAt: "2026-08-22T12:00:00Z",
};

beforeEach(() => {
  vi.mocked(api.ocrScreenshot).mockReset().mockResolvedValue(OCR_REPLY);
  vi.mocked(api.addMarkdownAttachment).mockReset().mockResolvedValue({ id: "att-1" } as never);
});

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

  /// Delete is destructive and immediate; sitting it next to Close made a mis-aimed click lose a capture
  /// (#577). It now leads the trailing cluster, fenced off by a separator from every routine control.
  describe("delete placement", () => {
    /// True when `later` comes after `earlier` in document order.
    function precedes(earlier: Element, later: Element) {
      return Boolean(earlier.compareDocumentPosition(later) & Node.DOCUMENT_POSITION_FOLLOWING);
    }

    function renderWithDelete() {
      render(
        <ScreenshotModal
          recordingId="r1"
          shots={shots}
          index={0}
          onIndexChange={() => {}}
          onClose={() => {}}
          onDelete={() => {}}
        />,
      );
      return {
        del: screen.getByRole("button", { name: /delete screenshot/i }),
        separator: screen.getByRole("separator"),
        close: screen.getByRole("button", { name: /close screenshot/i }),
        download: screen.getByRole("link", { name: /download screenshot/i }),
        expand: screen.getByRole("button", { name: /enter full screen/i }),
      };
    }

    it("puts the delete control before the download, full-screen and close controls", () => {
      const { del, close, download, expand } = renderWithDelete();

      expect(precedes(del, download)).toBe(true);
      expect(precedes(del, expand)).toBe(true);
      expect(precedes(del, close)).toBe(true);
    });

    it("fences the delete control off with a separator", () => {
      const { del, separator, close } = renderWithDelete();

      expect(precedes(del, separator)).toBe(true);
      expect(precedes(separator, close)).toBe(true);
    });

    it("renders no separator when there is no delete control to fence off", () => {
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />);

      expect(screen.queryByRole("separator")).toBeNull();
    });
  });

  describe("add to chat context", () => {
    it("publishes the capture on screen to the chat composer", () => {
      const attached = vi.fn();
      const off = onChatScreenshotAttached(attached);
      render(<ScreenshotModal recordingId="r1" shots={shots} index={1} onIndexChange={() => {}} onClose={() => {}} />);

      fireEvent.click(screen.getByRole("button", { name: /add to chat context/i }));

      expect(attached).toHaveBeenCalledWith({ recordingId: "r1", screenshotId: "b" });
      off();
    });

    it("stays open so the capture can still be read while asking about it", () => {
      const onClose = vi.fn();
      const off = onChatScreenshotAttached(() => {});
      render(<ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={onClose} />);

      fireEvent.click(screen.getByRole("button", { name: /add to chat context/i }));

      expect(onClose).not.toHaveBeenCalled();
      off();
    });

    /// The chat panel is behind the viewer, so the only feedback a click can give is on the button itself.
    it("confirms the capture was added, and drops the confirmation on moving to another capture", () => {
      const off = onChatScreenshotAttached(() => {});
      const { rerender } = render(
        <ScreenshotModal recordingId="r1" shots={shots} index={0} onIndexChange={() => {}} onClose={() => {}} />,
      );

      fireEvent.click(screen.getByRole("button", { name: /add to chat context/i }));
      expect(screen.getByRole("button", { name: /added to chat context/i })).toBeTruthy();

      rerender(
        <ScreenshotModal recordingId="r1" shots={shots} index={1} onIndexChange={() => {}} onClose={() => {}} />,
      );
      expect(screen.getByRole("button", { name: /add to chat context/i })).toBeTruthy();
      off();
    });
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

describe("ScreenshotModal - extract text (OCR)", () => {
  const props = {
    recordingId: "r1",
    shots,
    index: 0,
    onIndexChange: () => {},
    onClose: () => {},
  };

  /// Offering an action nobody has configured is worse than not offering it: the endpoint would 400 every
  /// time, and most deployments never route an OCR model.
  it("draws neither extract button when no OCR model is routed", () => {
    render(<ScreenshotModal {...props} />);

    expect(screen.queryByRole("button", { name: /extract text to chat/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /extract text as an attachment/i })).toBeNull();
  });

  it("draws both extract buttons when OCR is available", () => {
    render(<ScreenshotModal {...props} ocrEnabled />);

    expect(screen.getByRole("button", { name: /extract text to chat/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /extract text as an attachment/i })).toBeTruthy();
  });

  /// The image button is NOT replaced by these: it is still the right action for a general vision model,
  /// where the user wants the model to see the capture rather than read text off it.
  it("keeps the image attach button alongside them", () => {
    render(<ScreenshotModal {...props} ocrEnabled />);

    expect(screen.getByRole("button", { name: /add to chat context/i })).toBeTruthy();
  });

  it("publishes the extracted text to the chat composer", async () => {
    const received: { name: string; text: string }[] = [];
    const off = onChatTextAttached((t) => received.push(t));

    render(<ScreenshotModal {...props} ocrEnabled />);
    fireEvent.click(screen.getByRole("button", { name: /extract text to chat/i }));

    await waitFor(() => expect(received.length).toBe(1));
    expect(received[0].text).toContain("Extracted text");
    expect(received[0].name).toMatch(/1:05/);
    // Provenance rides the chat path too, not just the attachment: text pasted into a model's context
    // with no note of where it came from is exactly how an invented number becomes a quoted fact.
    expect(received[0].text).toContain("olmocr-2-7b-1025");
    expect(received[0].text).toMatch(/unverified/i);
    off();
  });

  it("saves the extracted text as a Markdown attachment", async () => {
    render(<ScreenshotModal {...props} ocrEnabled />);
    fireEvent.click(screen.getByRole("button", { name: /extract text as an attachment/i }));

    await waitFor(() => expect(api.addMarkdownAttachment).toHaveBeenCalled());
    const [recordingId, name, content] = vi.mocked(api.addMarkdownAttachment).mock.calls[0];
    expect(recordingId).toBe("r1");
    expect(name).toMatch(/1:05/);
    expect(content).toContain("Extracted text");
  });

  /// Provenance is not decoration. Measured against four models, every one produced silent errors on a
  /// dense capture - and one invented a whole column of plausible scores. Text that reads as transcribed
  /// fact is the one way this feature does harm.
  it("stamps the attachment with the model and an unverified warning", async () => {
    render(<ScreenshotModal {...props} ocrEnabled />);
    fireEvent.click(screen.getByRole("button", { name: /extract text as an attachment/i }));

    await waitFor(() => expect(api.addMarkdownAttachment).toHaveBeenCalled());
    const content = vi.mocked(api.addMarkdownAttachment).mock.calls[0][2];
    expect(content).toContain("olmocr-2-7b-1025");
    expect(content).toMatch(/unverified/i);
  });

  it("surfaces a failure on the button rather than failing silently", async () => {
    vi.mocked(api.ocrScreenshot).mockRejectedValueOnce(new Error("no OCR model"));

    render(<ScreenshotModal {...props} ocrEnabled />);
    fireEvent.click(screen.getByRole("button", { name: /extract text to chat/i }));

    await waitFor(() => expect(screen.getByRole("alert")).toBeTruthy());
  });

  /// The model's answer arrives as HTML when the page had a table on it - which is the useful case, not a
  /// defect - so what lands in a note or a chat context has to be Markdown by the time it gets there.
  it("converts an HTML table in the model's answer to a Markdown table", async () => {
    vi.mocked(api.ocrScreenshot).mockResolvedValueOnce({
      text: "<table><tr><th>Core requirement</th><th>Total</th></tr><tr><td>USP</td><td>11</td></tr></table>",
      model: "olmocr-2-7b-1025",
      chars: 90,
      cached: false,
      generatedAt: "2026-08-22T12:00:00Z",
    });

    render(<ScreenshotModal {...props} ocrEnabled />);
    fireEvent.click(screen.getByRole("button", { name: /extract text as an attachment/i }));

    await waitFor(() => expect(api.addMarkdownAttachment).toHaveBeenCalled());
    const content = vi.mocked(api.addMarkdownAttachment).mock.calls[0][2];
    expect(content).toContain("| Core requirement | Total |");
    expect(content).toContain("| --- | --- |");
    expect(content).toContain("| USP | 11 |");
    expect(content).not.toContain("<table>");
  });

  /// The Files tab is rendered by the page underneath this modal, so nothing about saving an attachment
  /// from up here would otherwise reach it.
  it("tells the page to refresh its files after saving an attachment", async () => {
    const onAttachmentSaved = vi.fn();
    render(<ScreenshotModal {...props} ocrEnabled onAttachmentSaved={onAttachmentSaved} />);

    fireEvent.click(screen.getByRole("button", { name: /extract text as an attachment/i }));

    await waitFor(() => expect(onAttachmentSaved).toHaveBeenCalledTimes(1));
  });

  /// Sending to chat writes no attachment, so asking the page to refresh its files would be a pointless
  /// refetch on every extraction.
  it("does not ask for a files refresh when the text went to chat", async () => {
    const onAttachmentSaved = vi.fn();
    const off = onChatTextAttached(() => {});
    render(<ScreenshotModal {...props} ocrEnabled onAttachmentSaved={onAttachmentSaved} />);

    fireEvent.click(screen.getByRole("button", { name: /extract text to chat/i }));

    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());
    expect(onAttachmentSaved).not.toHaveBeenCalled();
    off();
  });

  /// A cold model load can take a minute, so the button has to say it is working - and must not accept a
  /// second click that would spend a second call for the same answer.
  it("shows progress and blocks both buttons while a run is in flight", async () => {
    let release: (v: unknown) => void = () => {};
    vi.mocked(api.ocrScreenshot).mockReturnValueOnce(
      new Promise((resolve) => {
        release = resolve;
      }) as ReturnType<typeof api.ocrScreenshot>,
    );

    render(<ScreenshotModal {...props} ocrEnabled />);
    const toChat = screen.getByRole("button", { name: /extract text to chat/i });
    fireEvent.click(toChat);

    await waitFor(() => expect(toChat.querySelector(".animate-spin")).toBeTruthy());
    expect(toChat.hasAttribute("disabled")).toBe(true);
    expect(screen.getByRole("button", { name: /extract text as an attachment/i }).hasAttribute("disabled")).toBe(true);

    release({ text: "Extracted text", model: "m", chars: 14, cached: false, generatedAt: "" });
    await waitFor(() => expect(toChat.hasAttribute("disabled")).toBe(false));
  });

  it("marks the button that succeeded with a tick", async () => {
    render(<ScreenshotModal {...props} ocrEnabled />);
    const toChat = screen.getByRole("button", { name: /extract text to chat/i });
    const toFile = screen.getByRole("button", { name: /extract text as an attachment/i });

    fireEvent.click(toChat);

    // The tick replaces the destination glyph on the button that ran, and only on that one.
    await waitFor(() => expect(toChat.querySelectorAll("svg")).toHaveLength(2));
    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());
    expect(toFile.querySelector(".animate-spin")).toBeNull();
  });
});
