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

  // 72px = the left panel's first two rows, so the bar's lower edge lands on the panel's second divider.
  // 36 + 36, not 37 + 37: Tailwind's preflight makes every element border-box, so a row's h-9 already
  // contains its 1px border-b. This bar is border-box too, so its own bottom border is inside the 72.
  // jsdom has no layout engine, so this pins the number the browser measurement established rather than
  // recomputing the alignment - if the panel's row heights change, re-measure and change both together.
  // There is no window edge above the bar any more, so the old 2px top border goes with the header.
  it("is 72px tall with a bottom border and no top border", () => {
    const { container } = renderBar();
    const bar = container.firstElementChild as HTMLElement;
    expect(bar.style.height).toBe("72px");
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

  // The bar's own chrome is 68px of dead space around the cluster - 36px of padding plus the two gap-4
  // gutters to the centring spacers - which is a quarter of the whole bar once the cluster is down to its
  // icons. One shared threshold rather than the per-state ones the cluster uses: the bar does not know
  // whether a recording is running, and 44px is noise at the recording tier's 690px. jsdom computes no
  // geometry and loads no Tailwind CSS, so this proves the classes are present, not the resulting width.
  it("tightens its own padding and gutters on a narrow bar", () => {
    const { container } = renderBar();
    const bar = container.firstElementChild as HTMLElement;
    expect(bar.className).toContain("px-[18px]");
    expect(bar.className).toContain("@max-[480px]:px-2");
    expect(bar.className).toContain("gap-4");
    expect(bar.className).toContain("@max-[480px]:gap-1");
    // Moved off the inline style, or the class could never win.
    expect(bar.style.padding).toBe("");
  });
});
