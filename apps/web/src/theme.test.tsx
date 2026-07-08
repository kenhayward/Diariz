import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("./lib/api", () => ({ api: { getProfile: vi.fn() } }));

import { api } from "./lib/api";
import { ThemeProvider, useTheme } from "./theme";
import ThemeSync from "./components/ThemeSync";
import type { ThemeChoice } from "./lib/theme";

function Probe() {
  const { theme, setTheme } = useTheme();
  return (
    <div>
      <span data-testid="theme">{theme}</span>
      <button onClick={() => setTheme("dark")}>go dark</button>
    </div>
  );
}

function renderThemed() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ThemeProvider>
        <ThemeSync />
        <Probe />
      </ThemeProvider>
    </QueryClientProvider>,
  );
}

describe("ThemeProvider + ThemeSync", () => {
  beforeEach(() => {
    localStorage.clear();
    // jsdom has no matchMedia; the provider queries it on every apply.
    window.matchMedia = vi.fn().mockReturnValue({
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }) as unknown as typeof window.matchMedia;
  });

  it("keeps a user's theme choice - the server sync does not clobber it", async () => {
    // Server theme differs from the initial (localStorage was cleared -> "auto"), so adopting it to "light"
    // proves the profile has loaded before we make a change.
    vi.mocked(api.getProfile).mockResolvedValue({ theme: "light" as ThemeChoice } as never);
    renderThemed();

    // The server profile ("light") is adopted once loaded.
    await waitFor(() => expect(screen.getByTestId("theme").textContent).toBe("light"));

    // The user picks dark; it must stick (the sync effect must not revert it to the server's "light").
    fireEvent.click(screen.getByText("go dark"));
    expect(screen.getByTestId("theme").textContent).toBe("dark");
    await waitFor(() => expect(screen.getByTestId("theme").textContent).toBe("dark"));
  });
});
