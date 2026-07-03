import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listMcpTokens: vi.fn(),
    createMcpToken: vi.fn(),
    revokeMcpToken: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import McpAccessSection from "./McpAccessSection";

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <McpAccessSection />
    </QueryClientProvider>,
  );
}

describe("McpAccessSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listMcpTokens as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "t1", name: "Claude Desktop", prefix: "dz_mcp_ab12cd", createdAt: "2026-07-01T00:00:00Z", lastUsedAt: null },
    ]);
    (api.createMcpToken as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "t2",
      name: "New",
      prefix: "dz_mcp_zz99yy",
      token: "dz_mcp_THE_SECRET_TOKEN",
    });
    (api.revokeMcpToken as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("lists existing tokens by name and prefix", async () => {
    renderSection();
    expect(await screen.findByText(/Claude Desktop/)).toBeTruthy();
    expect(screen.getByText(/dz_mcp_ab12cd/)).toBeTruthy();
  });

  it("generates a token and shows the plaintext once", async () => {
    renderSection();
    await screen.findByText(/Claude Desktop/);
    fireEvent.change(screen.getByLabelText(/Token name/i), { target: { value: "Laptop" } });
    fireEvent.click(screen.getByRole("button", { name: /Generate token/i }));

    await waitFor(() => expect(api.createMcpToken).toHaveBeenCalledWith("Laptop"));
    expect(await screen.findByText("dz_mcp_THE_SECRET_TOKEN")).toBeTruthy();
    expect(screen.getByText(/won't be able to see it again/i)).toBeTruthy();
  });

  it("revokes a token", async () => {
    renderSection();
    await screen.findByText(/Claude Desktop/);
    fireEvent.click(screen.getByRole("button", { name: /Revoke/i }));
    await waitFor(() => expect(api.revokeMcpToken).toHaveBeenCalledWith("t1"));
  });
});
