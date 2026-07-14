import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi } from "vitest";
import TopBar from "../TopBar";

// Keep this a shell test: stub the heavy children so we only assert the bar frame + regions.
vi.mock("../Recorder", () => ({
  default: () => <div data-testid="recorder-stub">recorder</div>,
}));
vi.mock("../UserMenu", () => ({
  default: () => (
    <button data-tour="account" data-testid="usermenu-stub">
      account
    </button>
  ),
}));
vi.mock("../ThemeSync", () => ({
  default: () => null,
}));

function renderBar() {
  const qc = new QueryClient();
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <TopBar />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("TopBar frame", () => {
  it("renders a header", () => {
    const { container } = renderBar();
    expect(container.querySelector("header")).toBeTruthy();
  });

  it("renders the logo image and the wordmark", () => {
    const { container, getByText } = renderBar();
    const logo = container.querySelector('img[src="/logo.png"]');
    expect(logo).toBeTruthy();
    expect(getByText("Diariz")).toBeTruthy();
  });

  it("mounts the Recorder inside the capture cluster", () => {
    const { container } = renderBar();
    const cluster = container.querySelector('[data-tour="capture"]');
    expect(cluster).toBeTruthy();
    expect(cluster?.querySelector('[data-testid="recorder-stub"]')).toBeTruthy();
  });

  it("mounts the account avatar (UserMenu)", () => {
    const { container } = renderBar();
    expect(container.querySelector('[data-tour="account"]')).toBeTruthy();
  });
});
