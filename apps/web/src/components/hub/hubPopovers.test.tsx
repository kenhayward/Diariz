import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import { HubPopoverProvider, useHubPopover, type HubPopoverId } from "./hubPopovers";

// A tiny harness that exercises the context: a button per id that toggles it, plus a live readout of
// which popover the shared state reports open.
function Probe() {
  const { openId, toggle, close, isOpen } = useHubPopover();
  const ids: HubPopoverId[] = ["source", "stop", "notes", "acct"];
  return (
    <div>
      <span data-testid="open-id">{openId ?? "none"}</span>
      {ids.map((id) => (
        <button key={id} onClick={() => toggle(id)}>
          toggle-{id}
        </button>
      ))}
      <button onClick={close}>close</button>
      <span data-testid="source-open">{isOpen("source") ? "yes" : "no"}</span>
    </div>
  );
}

describe("useHubPopover", () => {
  it("opens the toggled popover and reports it as the single open one", () => {
    render(
      <HubPopoverProvider>
        <Probe />
      </HubPopoverProvider>,
    );
    expect(screen.getByTestId("open-id").textContent).toBe("none");

    fireEvent.click(screen.getByText("toggle-source"));
    expect(screen.getByTestId("open-id").textContent).toBe("source");
    expect(screen.getByTestId("source-open").textContent).toBe("yes");
  });

  it("toggling the same id again closes it", () => {
    render(
      <HubPopoverProvider>
        <Probe />
      </HubPopoverProvider>,
    );
    fireEvent.click(screen.getByText("toggle-source"));
    fireEvent.click(screen.getByText("toggle-source"));
    expect(screen.getByTestId("open-id").textContent).toBe("none");
  });

  it("opening one popover closes any other (one open at a time)", () => {
    render(
      <HubPopoverProvider>
        <Probe />
      </HubPopoverProvider>,
    );
    fireEvent.click(screen.getByText("toggle-source"));
    fireEvent.click(screen.getByText("toggle-notes"));
    expect(screen.getByTestId("open-id").textContent).toBe("notes");
    expect(screen.getByTestId("source-open").textContent).toBe("no");
  });

  it("close() clears the open popover", () => {
    render(
      <HubPopoverProvider>
        <Probe />
      </HubPopoverProvider>,
    );
    fireEvent.click(screen.getByText("toggle-acct"));
    fireEvent.click(screen.getByText("close"));
    expect(screen.getByTestId("open-id").textContent).toBe("none");
  });

  // Used outside a provider, the hook still works via a self-contained local fallback so a component
  // (e.g. Recorder) can be rendered in isolation without wrapping it in the provider.
  it("falls back to a working local state when used outside a provider", () => {
    render(<Probe />);
    expect(screen.getByTestId("open-id").textContent).toBe("none");
    fireEvent.click(screen.getByText("toggle-source"));
    expect(screen.getByTestId("open-id").textContent).toBe("source");
    fireEvent.click(screen.getByText("close"));
    expect(screen.getByTestId("open-id").textContent).toBe("none");
  });
});
