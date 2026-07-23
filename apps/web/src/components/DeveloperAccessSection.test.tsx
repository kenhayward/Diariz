import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { listApiTokens: vi.fn(), createApiToken: vi.fn(), revokeApiToken: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));
import { api } from "../lib/api";
import DeveloperAccessSection from "./DeveloperAccessSection";

function renderIt() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <DeveloperAccessSection />
    </QueryClientProvider>,
  );
}

describe("DeveloperAccessSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listApiTokens as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.createApiToken as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "t1", name: "x", prefix: "dz_api_ab12cd", token: "dz_api_secret",
    });
    (api.revokeApiToken as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("links to the API reference in a new tab", async () => {
    renderIt();
    const link = await screen.findByRole("link", { name: /view api reference/i });
    expect(link.getAttribute("href")).toBe("/developers/api");
    expect(link.getAttribute("target")).toBe("_blank");
  });

  it("generates a token and shows it once", async () => {
    renderIt();
    fireEvent.click(await screen.findByRole("button", { name: /generate token/i }));
    await waitFor(() => expect(api.createApiToken).toHaveBeenCalled());
    expect(await screen.findByText("dz_api_secret")).toBeTruthy();
  });

  it("lists existing tokens and revokes one", async () => {
    (api.listApiTokens as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "t9", name: "CI", prefix: "dz_api_zz99", createdAt: new Date().toISOString(), lastUsedAt: null },
    ]);
    renderIt();
    expect(await screen.findByText(/CI/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /revoke/i }));
    await waitFor(() => expect(api.revokeApiToken).toHaveBeenCalledWith("t9"));
  });

  it("passes read-only when the box is ticked", async () => {
    const createApiToken = vi.mocked(api.createApiToken).mockResolvedValue({
      id: "1", name: "t", prefix: "dz_api_x", token: "dz_api_secret",
    });
    renderIt();
    fireEvent.click(await screen.findByLabelText(/read-only/i));
    fireEvent.click(screen.getByRole("button", { name: /generate/i }));
    await waitFor(() =>
      expect(createApiToken).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ readOnly: true })));
  });
});
