import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import MoreControlsPopover from "./MoreControlsPopover";

// The narrow-window overflow menu. Which rows exist is expressed by which handlers the Recorder passes,
// not by a `recording` flag - the two states differ only in which controls are available, and the
// component has no business knowing why.
describe("MoreControlsPopover", () => {
  it("renders nothing while closed", () => {
    const { container } = render(
      <MoreControlsPopover open={false} onClose={() => {}} onAutoStop={() => {}} />,
    );
    expect(container.textContent).toBe("");
  });

  it("offers only the rows it was given a handler for", () => {
    render(<MoreControlsPopover open onClose={() => {}} onAutoStop={() => {}} onUpload={() => {}} />);

    expect(screen.getByRole("button", { name: "Auto-stop" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Upload" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Notes" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Capture screenshot" })).toBeNull();
  });

  it("offers the recording-only rows when their handlers are given", () => {
    render(
      <MoreControlsPopover
        open
        onClose={() => {}}
        onAutoStop={() => {}}
        onCapture={() => {}}
        onNotes={() => {}}
      />,
    );

    expect(screen.getByRole("button", { name: "Capture screenshot" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Notes" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Upload" })).toBeNull();
  });

  it("invokes the chosen row's handler", () => {
    const onNotes = vi.fn();
    render(<MoreControlsPopover open onClose={() => {}} onAutoStop={() => {}} onNotes={onNotes} />);

    fireEvent.click(screen.getByRole("button", { name: "Notes" }));

    expect(onNotes).toHaveBeenCalledTimes(1);
  });

  // Same two-flavours-of-unavailable rule HubIconButton documents: a row with a reason stays hoverable so
  // the reason can be read, and is inert by handler rather than `disabled` (Chromium never renders a
  // disabled control's title). A row that is merely greyed with no explanation is the thing to avoid.
  it("makes a row with a reason inert, hoverable, and titled with the reason", () => {
    const onCapture = vi.fn();
    render(
      <MoreControlsPopover
        open
        onClose={() => {}}
        onAutoStop={() => {}}
        onCapture={onCapture}
        captureDisabledReason="Set a capture area first"
      />,
    );
    const row = screen.getByRole("button", { name: "Capture screenshot" });

    fireEvent.click(row);

    expect(onCapture).not.toHaveBeenCalled();
    expect(row.getAttribute("title")).toBe("Set a capture area first");
    expect(row.getAttribute("aria-disabled")).toBe("true");
    expect((row as HTMLButtonElement).disabled).toBe(false);
  });

  it("closes on the backdrop", () => {
    const onClose = vi.fn();
    render(<MoreControlsPopover open onClose={onClose} onAutoStop={() => {}} />);

    fireEvent.click(screen.getByTestId("hub-popover-backdrop"));

    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
