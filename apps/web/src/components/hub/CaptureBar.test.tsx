import { render } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi } from "vitest";
import CaptureBar from "./CaptureBar";

// Keep this a shell test: stub the recorder so we only assert the bar frame + regions.
vi.mock("../Recorder", () => ({
  default: () => <div data-testid="recorder-stub">recorder</div>,
}));

function renderBar() {
  const qc = new QueryClient();
  return render(
    <QueryClientProvider client={qc}>
      <CaptureBar />
    </QueryClientProvider>,
  );
}

describe("CaptureBar", () => {
  it("mounts the Recorder inside the capture cluster", () => {
    const { container } = renderBar();
    const cluster = container.querySelector('[data-tour="capture"]');
    expect(cluster).toBeTruthy();
    expect(cluster?.querySelector('[data-testid="recorder-stub"]')).toBeTruthy();
  });

  // The brand block is gone: the browser tab already carries the icon and the name.
  it("carries no brand mark or wordmark", () => {
    const { container, queryByText } = renderBar();
    expect(container.querySelector('img[src="/logo.png"]')).toBeNull();
    expect(queryByText("Diariz")).toBeNull();
  });

  // 73px + the 1px bottom border = 74px = the left panel's first two rows. There is no window edge above
  // the bar any more, so the old 2px top border goes with the header.
  it("is 73px tall with a bottom border and no top border", () => {
    const { container } = renderBar();
    const bar = container.firstElementChild as HTMLElement;
    expect(bar.style.height).toBe("73px");
    expect(bar.style.borderBottom).toContain("var(--hub-bar-border-bottom)");
    expect(bar.style.borderTop).toBe("");
  });
});
