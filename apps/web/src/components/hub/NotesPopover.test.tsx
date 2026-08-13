import { render, screen, fireEvent } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import NotesPopover, { type NotesPopoverProps } from "./NotesPopover";
import type { PendingShot } from "../../lib/pendingScreenshots";

let nextShotId = 0;
const shot = (capturedAtMs: number): PendingShot => ({
  id: `shot-${nextShotId++}`,
  capturedAtMs,
  width: 10,
  height: 10,
  full: new Blob(["f"], { type: "image/png" }),
  thumb: new Blob(["t"], { type: "image/jpeg" }),
});

const baseProps: NotesPopoverProps = {
  open: true,
  onClose: () => {},
  lines: [],
  onAdd: () => {},
  onEdit: () => {},
  onDelete: () => {},
  shots: [],
  onDeleteShot: () => {},
};

function renderPopover(overrides: Partial<NotesPopoverProps> = {}) {
  return render(<NotesPopover {...baseProps} {...overrides} />);
}

describe("NotesPopover screenshots", () => {
  let urlCounter = 0;
  let createSpy: ReturnType<typeof vi.spyOn>;
  let revokeSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    urlCounter = 0;
    createSpy = vi.spyOn(URL, "createObjectURL").mockImplementation(() => `blob:mock-${urlCounter++}`);
    revokeSpy = vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
  });

  afterEach(() => {
    createSpy.mockRestore();
    revokeSpy.mockRestore();
  });

  it("shows one thumbnail per capture taken so far", () => {
    renderPopover({ shots: [shot(1_000), shot(2_000)], onChangeCaptureArea: () => {} });

    expect(screen.getAllByRole("img")).toHaveLength(2);
  });

  it("gives each thumbnail alt text distinguishing it from the others", () => {
    renderPopover({ shots: [shot(1_000), shot(65_000)], onChangeCaptureArea: () => {} });

    const images = screen.getAllByRole("img");
    expect(images[0].getAttribute("alt")).not.toBe(images[1].getAttribute("alt"));
  });

  it("rolls a capture's alt-text stamp over into h:mm:ss past one hour, matching the transcript/strip format", () => {
    renderPopover({ shots: [shot(3_904_000)], onChangeCaptureArea: () => {} }); // 1h 05m 04s

    expect(screen.getByRole("img").getAttribute("alt")).toContain("1:05:04");
  });

  it("offers changing the capture area", () => {
    const onChangeCaptureArea = vi.fn();
    renderPopover({ shots: [shot(1_000)], onChangeCaptureArea });

    fireEvent.click(screen.getByRole("button", { name: /change capture area/i }));

    expect(onChangeCaptureArea).toHaveBeenCalledTimes(1);
  });

  it("shows no screenshot area when the shell cannot capture", () => {
    renderPopover({ shots: [], onChangeCaptureArea: undefined });

    expect(screen.queryByRole("button", { name: /change capture area/i })).toBeNull();
    expect(screen.queryByRole("img")).toBeNull();
  });

  it("offers capturing a screenshot without leaving the popover", () => {
    const onCapture = vi.fn();
    renderPopover({ shots: [], onChangeCaptureArea: () => {}, onCapture });

    fireEvent.click(screen.getByRole("button", { name: /capture screenshot/i }));

    expect(onCapture).toHaveBeenCalledTimes(1);
  });

  it("hides the capture button when onCapture is not provided", () => {
    renderPopover({ shots: [], onChangeCaptureArea: () => {}, onCapture: undefined });

    expect(screen.queryByRole("button", { name: /capture screenshot/i })).toBeNull();
  });

  // Capturing with no area chosen opens the area picker and leaves BOTH buttons inert until it is
  // dismissed, which reads as "the buttons stopped working". Capture stays disabled until an area exists.
  it("disables the capture button until a capture area has been set", () => {
    const onCapture = vi.fn();
    renderPopover({ shots: [], onChangeCaptureArea: () => {}, onCapture, captureAreaSet: false });

    const button = screen.getByRole<HTMLButtonElement>("button", { name: /capture screenshot/i });
    expect(button.disabled).toBe(true);

    fireEvent.click(button);
    expect(onCapture).not.toHaveBeenCalled();
  });

  it("enables the capture button once a capture area is set", () => {
    const onCapture = vi.fn();
    renderPopover({ shots: [], onChangeCaptureArea: () => {}, onCapture, captureAreaSet: true });

    const button = screen.getByRole<HTMLButtonElement>("button", { name: /capture screenshot/i });
    expect(button.disabled).toBe(false);

    fireEvent.click(button);
    expect(onCapture).toHaveBeenCalledTimes(1);
  });

  it("keeps setting the capture area available while capture is disabled", () => {
    const onChangeCaptureArea = vi.fn();
    renderPopover({ shots: [], onChangeCaptureArea, onCapture: () => {}, captureAreaSet: false });

    fireEvent.click(screen.getByRole("button", { name: /capture area/i }));

    expect(onChangeCaptureArea).toHaveBeenCalledTimes(1);
  });

  it("renders the strip with no captures yet, without a stray thumbnail", () => {
    renderPopover({ shots: [], onChangeCaptureArea: () => {} });

    expect(screen.queryByRole("img")).toBeNull();
    expect(screen.getByRole("button", { name: /change capture area/i })).toBeTruthy();
  });

  it("deletes the capture under the clicked button, naming it rather than its position", () => {
    const onDeleteShot = vi.fn();
    const shots = [shot(1_000), shot(2_000), shot(3_000)];
    renderPopover({ shots, onChangeCaptureArea: () => {}, onDeleteShot });

    const deleteButtons = screen.getAllByRole("button", { name: /delete screenshot/i });
    fireEvent.click(deleteButtons[1]);

    expect(onDeleteShot).toHaveBeenCalledTimes(1);
    expect(onDeleteShot).toHaveBeenCalledWith(shots[1].id);
  });

  it("revokes the previous object URLs when the capture set changes", () => {
    const { rerender } = renderPopover({ shots: [shot(1_000)], onChangeCaptureArea: () => {} });
    expect(createSpy).toHaveBeenCalledTimes(1);

    rerender(<NotesPopover {...baseProps} shots={[shot(1_000), shot(2_000)]} onChangeCaptureArea={() => {}} />);

    expect(revokeSpy).toHaveBeenCalledWith("blob:mock-0");
    expect(createSpy).toHaveBeenCalledTimes(3);
  });

  it("revokes object URLs on unmount", () => {
    const { unmount } = renderPopover({ shots: [shot(1_000), shot(2_000)], onChangeCaptureArea: () => {} });

    unmount();

    expect(revokeSpy).toHaveBeenCalledWith("blob:mock-0");
    expect(revokeSpy).toHaveBeenCalledWith("blob:mock-1");
  });
});

describe("NotesPopover pop-out control", () => {
  it("offers no pop-out control in a plain browser", () => {
    renderPopover();

    expect(screen.queryByRole("button", { name: /separate window/i })).toBeNull();
  });

  it("pops out when the shell supports it", () => {
    const onPopOut = vi.fn();
    renderPopover({ onPopOut });

    fireEvent.click(screen.getByRole("button", { name: /separate window/i }));

    expect(onPopOut).toHaveBeenCalledTimes(1);
  });

  it("keeps its close button reachable alongside the pop-out control", () => {
    const onClose = vi.fn();
    renderPopover({ onPopOut: vi.fn(), onClose });

    fireEvent.click(screen.getByRole("button", { name: /close notes/i }));

    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
