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

  // The column this bar renders in can be much narrower than the window (window - left panel - chat
  // panel), which the recorder cluster's own content easily exceeds. min-w-0 overrides the flex item's
  // default automatic minimum size (its content's min-content width) so the cluster can shrink instead of
  // spilling out of the column. jsdom has no layout engine, so this only proves the shrink-enabling class
  // is present - it does not prove the layout actually fits at any given width. That needs a manual check
  // (1280px window, chat panel dragged toward its max) - see the PR/report for that result.
  it("lets the capture cluster shrink instead of forcing its content width", () => {
    const { container } = renderBar();
    const cluster = container.querySelector('[data-tour="capture"]');
    expect(cluster?.className).toContain("min-w-0");
  });
});
