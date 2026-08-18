import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../../lib/api", () => ({
  api: { getProfile: vi.fn(), listApiTokens: vi.fn(), createApiToken: vi.fn(), revokeApiToken: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));
import { api } from "../../lib/api";
import ApiCard from "./ApiCard";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

function renderCard() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ApiCard />
    </QueryClientProvider>,
  );
}

const openDialog = () => fireEvent.click(screen.getByRole("button", { name: /new token/i }));

describe("ApiCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.getProfile).mockResolvedValue({ apiAccessEnabled: true });
    mock(api.listApiTokens).mockResolvedValue([]);
    mock(api.createApiToken).mockResolvedValue({
      id: "t1", name: "x", prefix: "dz_api_ab12cd", token: "dz_api_secret",
    });
    mock(api.revokeApiToken).mockResolvedValue(undefined);
  });

  it("generates a token and shows it once", async () => {
    renderCard();
    await screen.findByRole("button", { name: /new token/i });
    openDialog();
    fireEvent.click(screen.getByRole("button", { name: /^generate$/i }));

    expect(await screen.findByText("dz_api_secret")).toBeTruthy();
  });

  it("lists existing tokens and revokes one", async () => {
    mock(api.listApiTokens).mockResolvedValue([
      { id: "t9", name: "CI", prefix: "dz_api_zz", scope: "ReadWrite", createdAt: "2026-07-01T00:00:00Z", lastUsedAt: null, expiresAt: null },
    ]);
    renderCard();

    expect(await screen.findByText(/CI/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Revoke CI" }));
    await waitFor(() => expect(api.revokeApiToken).toHaveBeenCalledWith("t9"));
  });

  // Read-only and the expiry are decided when the token is minted and can never be changed, so they live
  // in the generate dialog rather than sitting on the card looking like settings.
  it("passes read-only and an expiry chosen in the dialog", async () => {
    renderCard();
    await screen.findByRole("button", { name: /new token/i });
    openDialog();
    fireEvent.click(screen.getByLabelText(/read-only/i));
    fireEvent.change(screen.getByLabelText(/expires on/i), { target: { value: "2026-12-31" } });
    fireEvent.click(screen.getByRole("button", { name: /^generate$/i }));

    await waitFor(() => expect(api.createApiToken).toHaveBeenCalled());
    const [, opts] = mock(api.createApiToken).mock.calls[0];
    expect(opts.readOnly).toBe(true);
    expect(opts.expiresAt).toMatch(/^2026-12-31/);
  });

  // A choice that cannot be seen afterwards may as well not have been offered: the list now says which
  // tokens are read-only and when each one lapses. Both were already on the wire and never rendered.
  it("shows a token's read-only marking and expiry in the list", async () => {
    mock(api.listApiTokens).mockResolvedValue([
      { id: "t9", name: "Nightly export", prefix: "dz_api_zz", scope: "ReadOnly", createdAt: "2026-07-01T00:00:00Z", lastUsedAt: "2026-08-08T09:00:00Z", expiresAt: "2026-12-31T23:59:59Z" },
    ]);
    renderCard();

    await screen.findByText(/Nightly export/);
    expect(screen.getByText("read-only")).toBeTruthy();
    expect(screen.getByText(/expires/)).toBeTruthy();
  });

  it("explains itself and asks for nothing when API access is switched off", async () => {
    mock(api.getProfile).mockResolvedValue({ apiAccessEnabled: false });
    renderCard();

    expect(await screen.findByText(/API access is switched off/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /new token/i })).toBeNull();
    expect(api.listApiTokens).not.toHaveBeenCalled();
  });

  /// The reference used to be `<a target="_blank">`. In the installed PWA and the desktop shell that leaves
  /// Diariz for the system browser, where the user is not signed in - and the reference is behind the app
  /// login, so it renders nothing useful once you get there.
  it("does not link out of the app to reach the API reference", async () => {
    renderCard();
    await screen.findByRole("button", { name: /view api reference/i });

    expect(screen.queryByRole("link", { name: /view api reference/i })).toBeNull();
  });

  it("opens the reference in place", async () => {
    renderCard();

    fireEvent.click(await screen.findByRole("button", { name: /view api reference/i }));

    await waitFor(() => expect(screen.getByRole("dialog", { name: /api reference/i })).toBeTruthy());
  });
});
