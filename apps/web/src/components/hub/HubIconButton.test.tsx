import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import HubIconButton from "./HubIconButton";

const Glyph = () => <svg data-testid="glyph" />;

describe("HubIconButton", () => {
  it("names itself for assistive tech, since the glyph carries no text", () => {
    render(<HubIconButton label="Upload" onClick={() => {}}><Glyph /></HubIconButton>);

    expect(screen.getByRole("button", { name: "Upload" })).toBeTruthy();
  });

  it("falls back to the label as the tooltip when no longer description is given", () => {
    render(<HubIconButton label="Upload" onClick={() => {}}><Glyph /></HubIconButton>);

    expect(screen.getByRole("button").getAttribute("title")).toBe("Upload");
  });

  it("shows the longer description on hover while keeping the short accessible name", () => {
    render(
      <HubIconButton label="Upload" title="Upload an audio file to transcribe" onClick={() => {}}>
        <Glyph />
      </HubIconButton>,
    );

    const button = screen.getByRole("button", { name: "Upload" });
    expect(button.getAttribute("title")).toBe("Upload an audio file to transcribe");
  });

  it("calls its handler once per click", () => {
    const onClick = vi.fn();
    render(<HubIconButton label="Upload" onClick={onClick}><Glyph /></HubIconButton>);

    fireEvent.click(screen.getByRole("button"));

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  describe("hard-disabled (nothing to explain)", () => {
    it("disables the control outright", () => {
      render(<HubIconButton label="Upload" onClick={() => {}} disabled><Glyph /></HubIconButton>);

      expect((screen.getByRole("button") as HTMLButtonElement).disabled).toBe(true);
    });

    it("does not fire its handler", () => {
      const onClick = vi.fn();
      render(<HubIconButton label="Upload" onClick={onClick} disabled><Glyph /></HubIconButton>);

      fireEvent.click(screen.getByRole("button"));

      expect(onClick).not.toHaveBeenCalled();
    });
  });

  // The reason a control is unavailable is worth more than the control itself, and `disabled` throws it
  // away: Chromium does not dispatch mouse events to disabled form controls, so their `title` never
  // renders. On an icon-only button that leaves a greyed glyph and no way to find out why. `disabledReason`
  // is the inert-but-hoverable variant, and taking the reason as a string rather than a second boolean
  // makes it impossible to declare one without showing it.
  describe("disabledReason (inert, but able to say why)", () => {
    it("stays hoverable so its explanation can actually render", () => {
      render(
        <HubIconButton label="Capture screenshot" onClick={() => {}} disabledReason="Set a capture area first">
          <Glyph />
        </HubIconButton>,
      );

      expect((screen.getByRole("button") as HTMLButtonElement).disabled).toBe(false);
    });

    it("announces itself as disabled to assistive tech", () => {
      render(
        <HubIconButton label="Capture screenshot" onClick={() => {}} disabledReason="Set a capture area first">
          <Glyph />
        </HubIconButton>,
      );

      expect(screen.getByRole("button").getAttribute("aria-disabled")).toBe("true");
    });

    it("shows the reason as its tooltip, in place of any other description", () => {
      render(
        <HubIconButton
          label="Capture screenshot"
          title="Take one screenshot of the capture area now"
          onClick={() => {}}
          disabledReason="Set a capture area first"
        >
          <Glyph />
        </HubIconButton>,
      );

      expect(screen.getByRole("button").getAttribute("title")).toBe("Set a capture area first");
    });

    it("does not fire its handler, despite being clickable", () => {
      const onClick = vi.fn();
      render(
        <HubIconButton label="Capture screenshot" onClick={onClick} disabledReason="Set a capture area first">
          <Glyph />
        </HubIconButton>,
      );

      fireEvent.click(screen.getByRole("button"));

      expect(onClick).not.toHaveBeenCalled();
    });

    it("keeps its accessible name, so the button is still identifiable while unavailable", () => {
      render(
        <HubIconButton label="Capture screenshot" onClick={() => {}} disabledReason="Set a capture area first">
          <Glyph />
        </HubIconButton>,
      );

      expect(screen.getByRole("button", { name: "Capture screenshot" })).toBeTruthy();
    });
  });

  describe("size", () => {
    it("is a 44px command-hub button by default", () => {
      render(<HubIconButton label="Upload" onClick={() => {}}><Glyph /></HubIconButton>);

      expect(screen.getByRole("button").style.width).toBe("44px");
    });

    it("shrinks to 28px inside a popover, where three of them share one row", () => {
      render(<HubIconButton label="Upload" size="sm" onClick={() => {}}><Glyph /></HubIconButton>);

      expect(screen.getByRole("button").style.width).toBe("28px");
    });
  });
});
