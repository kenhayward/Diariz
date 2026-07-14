import { render, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import HubPopover from "./HubPopover";

describe("HubPopover", () => {
  it("renders nothing when closed", () => {
    const { container, queryByText } = render(
      <HubPopover open={false} onClose={() => {}}>
        <span>Panel body</span>
      </HubPopover>,
    );
    expect(queryByText("Panel body")).toBeNull();
    expect(container.querySelector('[role="dialog"]')).toBeNull();
  });

  it("renders the panel and its children when open", () => {
    const { getByText, container } = render(
      <HubPopover open onClose={() => {}} ariaLabel="Test popover">
        <span>Panel body</span>
      </HubPopover>,
    );
    const panel = container.querySelector('[role="dialog"]');
    expect(panel).toBeTruthy();
    expect(panel?.getAttribute("aria-label")).toBe("Test popover");
    expect(getByText("Panel body")).toBeTruthy();
  });

  it("renders a backdrop that calls onClose when clicked", () => {
    const onClose = vi.fn();
    const { container } = render(
      <HubPopover open onClose={onClose}>
        <span>Panel body</span>
      </HubPopover>,
    );
    const backdrop = container.querySelector('[data-testid="hub-popover-backdrop"]');
    expect(backdrop).toBeTruthy();
    fireEvent.click(backdrop!);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("calls onClose when Escape is pressed", () => {
    const onClose = vi.fn();
    render(
      <HubPopover open onClose={onClose}>
        <span>Panel body</span>
      </HubPopover>,
    );
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("does not listen for Escape while closed", () => {
    const onClose = vi.fn();
    render(
      <HubPopover open={false} onClose={onClose}>
        <span>Panel body</span>
      </HubPopover>,
    );
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).not.toHaveBeenCalled();
  });
});
