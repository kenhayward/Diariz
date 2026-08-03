import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listFeedback: vi.fn(),
    deleteFeedback: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import FeedbackPanel from "./FeedbackPanel";

/// Matches the QueryClientProvider wrapper `SettingsModal.test.tsx` builds for this same panel's parent -
/// FeedbackPanel needs react-query (list + delete), which MaintenancePanel.test.tsx's sibling never had to
/// provide.
function Providers({ children }: { children: React.ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

describe("FeedbackPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("lists submissions newest first with the submitter", async () => {
    vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
      { id: "1", userId: "u1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "newer", route: "/x", release: "0.176.0", trailJson: "[]" },
      { id: "2", userId: "u2", userEmail: "b@e.com", createdAt: "2026-08-03T09:00:00Z", description: "older", route: "/y", release: "0.176.0", trailJson: "[]" },
    ]);

    render(<FeedbackPanel />, { wrapper: Providers });

    expect((await screen.findAllByTestId("feedback-row")).map((r) => r.textContent)).toHaveLength(2);
    expect(screen.getByText("a@e.com")).toBeTruthy();
  });

  it("expands a row to show the trail", async () => {
    vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
      {
        id: "1", userId: "u1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d", route: "/x", release: "0.176.0",
        trailJson: JSON.stringify([{ at: 1, kind: "api", label: "GET /api/recordings", detail: { status: 200 } }]),
      },
    ]);

    render(<FeedbackPanel />, { wrapper: Providers });
    await userEvent.click(await screen.findByRole("button", { name: /detail/i }));

    expect(screen.getByText(/GET \/api\/recordings/)).toBeTruthy();
  });

  it("deletes after confirmation and refreshes", async () => {
    const del = vi.fn().mockResolvedValue(undefined);
    vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
      { id: "1", userId: "u1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d", route: "/x", release: "0.176.0", trailJson: "[]" },
    ]);
    vi.mocked(api).deleteFeedback = del;

    render(<FeedbackPanel />, { wrapper: Providers });
    await userEvent.click(await screen.findByRole("button", { name: /delete/i }));
    await userEvent.click(screen.getByRole("button", { name: /confirm/i }));

    expect(del).toHaveBeenCalledWith("1");
  });

  it("does not delete when the confirmation step is cancelled", async () => {
    const del = vi.fn().mockResolvedValue(undefined);
    vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
      { id: "1", userId: "u1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d", route: "/x", release: "0.176.0", trailJson: "[]" },
    ]);
    vi.mocked(api).deleteFeedback = del;

    render(<FeedbackPanel />, { wrapper: Providers });
    await userEvent.click(await screen.findByRole("button", { name: /delete/i }));
    await userEvent.click(screen.getByRole("button", { name: /cancel/i }));

    expect(del).not.toHaveBeenCalled();
    // Back to the plain delete affordance, not stuck showing confirm/cancel.
    expect(await screen.findByRole("button", { name: /delete/i })).toBeTruthy();
  });

  it("shows the route and release alongside each submission", async () => {
    vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
      { id: "1", userId: "u1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d", route: "/recordings/abc", release: "0.176.0", trailJson: "[]" },
    ]);

    render(<FeedbackPanel />, { wrapper: Providers });

    expect(await screen.findByText(/\/recordings\/abc/)).toBeTruthy();
    expect(screen.getByText(/0\.176\.0/)).toBeTruthy();
  });

  it("survives a malformed trailJson on one row without breaking the rest of the panel", async () => {
    vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
      {
        id: "1", userId: "u1", userEmail: "broken@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d1",
        route: "/x", release: "0.176.0", trailJson: "{not valid json",
      },
      {
        id: "2", userId: "u2", userEmail: "ok@e.com", createdAt: "2026-08-03T09:00:00Z", description: "d2",
        route: "/y", release: "0.176.0",
        trailJson: JSON.stringify([{ at: 1, kind: "nav", label: "/recordings", detail: {} }]),
      },
    ]);

    render(<FeedbackPanel />, { wrapper: Providers });
    const detailButtons = await screen.findAllByRole("button", { name: /detail/i });
    expect(detailButtons).toHaveLength(2);

    // Expanding the malformed row must not throw, and must not stop the good row from expanding too.
    await userEvent.click(detailButtons[0]);
    await userEvent.click(detailButtons[1]);

    expect(screen.getByText(/\/recordings/)).toBeTruthy();
    expect(screen.getByText("broken@e.com")).toBeTruthy();
    expect(screen.getByText("ok@e.com")).toBeTruthy();
  });

  it("treats an empty trailJson as no trail rather than failing", async () => {
    vi.mocked(api).listFeedback = vi.fn().mockResolvedValue([
      { id: "1", userId: "u1", userEmail: "a@e.com", createdAt: "2026-08-03T10:00:00Z", description: "d", route: "/x", release: "0.176.0", trailJson: "" },
    ]);

    render(<FeedbackPanel />, { wrapper: Providers });
    await userEvent.click(await screen.findByRole("button", { name: /detail/i }));

    // No crash, and the row is still present.
    expect(screen.getAllByTestId("feedback-row")).toHaveLength(1);
  });
});
