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

  it("renders the strip with no captures yet, without a stray thumbnail", () => {
    renderPopover({ shots: [], onChangeCaptureArea: () => {} });

    expect(screen.queryByRole("img")).toBeNull();
    expect(screen.getByRole("button", { name: /change capture area/i })).toBeTruthy();
  });

  it("deletes the capture under the clicked button, not a stale index", () => {
    const onDeleteShot = vi.fn();
    renderPopover({ shots: [shot(1_000), shot(2_000), shot(3_000)], onChangeCaptureArea: () => {}, onDeleteShot });

    const deleteButtons = screen.getAllByRole("button", { name: /delete screenshot/i });
    fireEvent.click(deleteButtons[1]);

    expect(onDeleteShot).toHaveBeenCalledTimes(1);
    expect(onDeleteShot).toHaveBeenCalledWith(1);
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
