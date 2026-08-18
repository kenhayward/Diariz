import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import CaptureControls, { type CaptureControlsProps } from "./CaptureControls";

const baseProps: CaptureControlsProps = {
  captureAreaSet: true,
  onCapture: () => {},
  onChangeArea: () => {},
  onToggleAutoCapture: () => {},
};

function renderControls(overrides: Partial<CaptureControlsProps> = {}) {
  return render(<CaptureControls {...baseProps} {...overrides} />);
}

const captureButton = () => screen.getByRole("button", { name: /capture screenshot/i });
const areaButton = () => screen.getByRole("button", { name: /change capture area/i });
const autoButton = () => screen.getByRole("button", { name: /auto-capture/i });

describe("CaptureControls", () => {
  // The buttons are icon-only, so the accessible name is the ONLY name they have. A glyph that shipped
  // without an aria-label would leave a screen-reader user with an unlabelled button, and these queries
  // (which match on accessible name) are what would fail.
  describe("naming", () => {
    it("names every control, even though none of them carries visible text", () => {
      renderControls();

      expect(autoButton()).toBeTruthy();
      expect(captureButton()).toBeTruthy();
      expect(areaButton()).toBeTruthy();
    });

    it("describes each control more fully on hover than its short name does", () => {
      renderControls();

      for (const button of [autoButton(), captureButton(), areaButton()]) {
        const title = button.getAttribute("title");
        expect(title, "every icon button needs a hover description").toBeTruthy();
        expect(title).not.toBe(button.getAttribute("aria-label"));
      }
    });
  });

  describe("acting", () => {
    it("captures a screenshot", () => {
      const onCapture = vi.fn();
      renderControls({ onCapture });

      fireEvent.click(captureButton());

      expect(onCapture).toHaveBeenCalledTimes(1);
    });

    it("changes the capture area", () => {
      const onChangeArea = vi.fn();
      renderControls({ onChangeArea });

      fireEvent.click(areaButton());

      expect(onChangeArea).toHaveBeenCalledTimes(1);
    });
  });

  // Capturing with no area chosen opens the shell's area picker and leaves every control inert until it
  // settles, which reads as the whole panel having frozen. So capture waits until the area exists - and
  // has to be able to say so, which a `disabled` button cannot (its tooltip never renders).
  describe("before a capture area is set", () => {
    it("does not capture", () => {
      const onCapture = vi.fn();
      renderControls({ onCapture, captureAreaSet: false });

      fireEvent.click(captureButton());

      expect(onCapture).not.toHaveBeenCalled();
    });

    it("says why, rather than leaving a greyed glyph with no explanation", () => {
      renderControls({ captureAreaSet: false });

      expect(captureButton().getAttribute("title")).toMatch(/capture area first/i);
    });

    it("stays hoverable, so that explanation can actually render", () => {
      renderControls({ captureAreaSet: false });

      expect((captureButton() as HTMLButtonElement).disabled).toBe(false);
      expect(captureButton().getAttribute("aria-disabled")).toBe("true");
    });

    it("keeps changing the capture area available - it is the way out of this state", () => {
      const onChangeArea = vi.fn();
      renderControls({ onChangeArea, captureAreaSet: false });

      fireEvent.click(areaButton());

      expect(onChangeArea).toHaveBeenCalledTimes(1);
    });
  });

  // The pop-out notes window disables the whole row when its channel to the host has gone: a click would
  // travel nowhere. Same treatment - inert, but able to say why. The reason comes from the host, which is
  // the side that knows what went wrong.
  describe("when the host is unreachable", () => {
    it("acts on nothing", () => {
      const onCapture = vi.fn();
      const onChangeArea = vi.fn();
      renderControls({ onCapture, onChangeArea, unavailableReason: "Not connected" });

      fireEvent.click(captureButton());
      fireEvent.click(areaButton());

      expect(onCapture).not.toHaveBeenCalled();
      expect(onChangeArea).not.toHaveBeenCalled();
    });

    it("marks every control unavailable to assistive tech", () => {
      renderControls({ unavailableReason: "Not connected" });

      expect(autoButton().getAttribute("aria-disabled")).toBe("true");
      expect(captureButton().getAttribute("aria-disabled")).toBe("true");
      expect(areaButton().getAttribute("aria-disabled")).toBe("true");
    });

    it("shows the host's reason on every control, not just the one that has its own gate", () => {
      renderControls({ unavailableReason: "Not connected" });

      expect(captureButton().getAttribute("title")).toBe("Not connected");
      expect(areaButton().getAttribute("title")).toBe("Not connected");
    });

    it("says the host is unreachable rather than blaming the missing capture area", () => {
      renderControls({ unavailableReason: "Not connected", captureAreaSet: false });

      expect(captureButton().getAttribute("title")).toBe("Not connected");
    });
  });
});

// The sticky one: while it is engaged Diariz is watching the screen and filing a screenshot every time
// it changes. That state has to be legible at a glance, because the popover it lives in is dismissible -
// a toggle you cannot tell is running is the failure mode this whole feature has to avoid.
describe("auto-capture", () => {
  it("is offered only when the shell can do it", () => {
    render(<CaptureControls {...baseProps} onToggleAutoCapture={undefined} />);

    expect(screen.queryByRole("button", { name: /auto-capture/i })).toBeNull();
  });

  it("turns on", () => {
    const onToggleAutoCapture = vi.fn();
    renderControls({ onToggleAutoCapture });

    fireEvent.click(autoButton());

    expect(onToggleAutoCapture).toHaveBeenCalledTimes(1);
  });

  it("says it is running, in its name and on hover", () => {
    const { rerender } = render(<CaptureControls {...baseProps} autoCapture onToggleAutoCapture={() => {}} />);

    expect(autoButton().getAttribute("aria-pressed")).toBe("true");
    expect(autoButton().getAttribute("title")).toMatch(/is on/i);

    rerender(<CaptureControls {...baseProps} autoCapture={false} onToggleAutoCapture={() => {}} />);
    expect(autoButton().getAttribute("title")).not.toMatch(/is on/i);
  });

  it("turns off again", () => {
    const onToggleAutoCapture = vi.fn();
    renderControls({ autoCapture: true, onToggleAutoCapture });

    fireEvent.click(autoButton());

    expect(onToggleAutoCapture).toHaveBeenCalledTimes(1);
  });

  // Unlike the tray item, which picks an area then starts, the in-app button waits - it can say why, and
  // a popover that opens a full-screen picker under the user's cursor is a worse surprise than a
  // disabled button that explains itself.
  it("waits for a capture area, and says so", () => {
    const onToggleAutoCapture = vi.fn();
    renderControls({ captureAreaSet: false, onToggleAutoCapture });

    expect(autoButton().getAttribute("title")).toMatch(/capture area first/i);
    fireEvent.click(autoButton());

    expect(onToggleAutoCapture).not.toHaveBeenCalled();
  });
});
