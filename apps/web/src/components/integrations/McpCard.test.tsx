import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../../lib/api", () => ({
  api: {
    getProfile: vi.fn(),
    listMcpTokens: vi.fn(),
    createMcpToken: vi.fn(),
    revokeMcpToken: vi.fn(),
    listOAuthConnections: vi.fn(),
    revokeOAuthConnection: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import McpCard from "./McpCard";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function renderCard() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <McpCard />
    </QueryClientProvider>,
  );
}

/// Generating moved into a dialog, so every creation test opens it first.
const openDialog = () => fireEvent.click(screen.getByRole("button", { name: /new token/i }));

describe("McpCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.getProfile).mockResolvedValue({ mcpAccessEnabled: true });
    mock(api.listMcpTokens).mockResolvedValue([
      { id: "t1", name: "Laptop bridge", prefix: "dz_mcp_ab12cd", createdAt: "2026-07-01T00:00:00Z", lastUsedAt: null },
    ]);
    mock(api.createMcpToken).mockResolvedValue({
      id: "t2",
      name: "New",
      prefix: "dz_mcp_zz99yy",
      token: "dz_mcp_THE_SECRET_TOKEN",
    });
    mock(api.revokeMcpToken).mockResolvedValue(undefined);
    // Previously unmocked, so the connections query rejected and the whole block silently rendered nothing.
    mock(api.listOAuthConnections).mockResolvedValue([]);
    mock(api.revokeOAuthConnection).mockResolvedValue(undefined);
  });

  // The card sub-line names Claude Desktop as an example client, so a token cannot be called that
  // here without the assertion matching the wrong element.
  it("lists existing tokens by name and prefix", async () => {
    renderCard();
    expect(await screen.findByText(/Laptop bridge/)).toBeTruthy();
    expect(screen.getByText(/dz_mcp_ab12cd/)).toBeTruthy();
  });

  it("generates a token and shows the plaintext once", async () => {
    renderCard();
    await screen.findByText(/Laptop bridge/);
    openDialog();
    fireEvent.change(screen.getByLabelText(/Token name/i), { target: { value: "Laptop" } });
    fireEvent.click(screen.getByRole("button", { name: /^generate$/i }));

    await waitFor(() => expect(api.createMcpToken).toHaveBeenCalledWith("Laptop"));
    expect(await screen.findByText("dz_mcp_THE_SECRET_TOKEN")).toBeTruthy();
    expect(screen.getByText(/won't be able to see it again/i)).toBeTruthy();
  });

  // The secret exists in exactly one place for exactly one moment, so the dialog must not close itself on
  // success and take it with it.
  it("keeps the dialog open on the secret until it is dismissed", async () => {
    renderCard();
    await screen.findByText(/Laptop bridge/);
    openDialog();
    fireEvent.click(screen.getByRole("button", { name: /^generate$/i }));

    expect(await screen.findByText("dz_mcp_THE_SECRET_TOKEN")).toBeTruthy();
    expect(screen.getByRole("dialog", { name: /new token/i })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^generate$/i })).toBeNull();
  });

  // "Revoke" reads identically on the API card; the accessible name has to say which token.
  it("revokes a token from a control that names it", async () => {
    renderCard();
    await screen.findByText(/Laptop bridge/);
    fireEvent.click(screen.getByRole("button", { name: "Revoke Laptop bridge" }));
    await waitFor(() => expect(api.revokeMcpToken).toHaveBeenCalledWith("t1"));
  });

  it("lists connected apps and disconnects one", async () => {
    mock(api.listOAuthConnections).mockResolvedValue([
      { id: "c1", clientName: "claude.ai", connectedAt: "2026-07-04T09:00:00Z", scopes: [] },
    ]);
    renderCard();

    expect(await screen.findByText("claude.ai")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Disconnect claude.ai" }));
    await waitFor(() => expect(api.revokeOAuthConnection).toHaveBeenCalledWith("c1"));
  });

  it("counts its tokens and apps in the card header", async () => {
    mock(api.listOAuthConnections).mockResolvedValue([
      { id: "c1", clientName: "claude.ai", connectedAt: null, scopes: [] },
    ]);
    renderCard();
    expect(await screen.findByText("1 token · 1 app")).toBeTruthy();
  });

  /// The platform switch existed but was never reported to the user, so this card used to offer controls
  /// that the server would refuse. It now says so instead, and asks for nothing it cannot deliver.
  it("explains itself and asks for nothing when MCP access is switched off", async () => {
    mock(api.getProfile).mockResolvedValue({ mcpAccessEnabled: false });
    renderCard();

    expect(await screen.findByText(/MCP access is switched off/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /new token/i })).toBeNull();
    expect(api.listMcpTokens).not.toHaveBeenCalled();
  });

  /// A server older than the flag omits it, and MCP is on by default - a missing value must not read as off.
  it("stays available when the server does not report the flag", async () => {
    mock(api.getProfile).mockResolvedValue({});
    renderCard();
    expect(await screen.findByRole("button", { name: /new token/i })).toBeTruthy();
  });
});
