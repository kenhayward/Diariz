import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

// A JWT-shaped token with a future exp, so AuthProvider treats the session as authed. It deliberately
// carries NO role claim: authority comes from the profile now.
const payload = btoa(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + 3600, sub: "u1" }));
const TOKEN = `eyJhbGciOiJIUzI1NiJ9.${payload}.sig`;

vi.mock("./lib/api", () => ({
  api: { refresh: vi.fn(), getProfile: vi.fn() },
  getToken: vi.fn(() => TOKEN),
  setToken: vi.fn(),
}));

import { api } from "./lib/api";
import { AuthProvider, useAuth } from "./auth";

function Probe() {
  const { isAdmin, isPlatformAdmin, canManageFormulas, permissions } = useAuth();
  return (
    <div>
      <span data-testid="admin">{String(isAdmin)}</span>
      <span data-testid="platform">{String(isPlatformAdmin)}</span>
      <span data-testid="formulas">{String(canManageFormulas)}</span>
      <span data-testid="rooms">{String(permissions.manageRooms)}</span>
    </div>
  );
}

function renderProbe() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <AuthProvider>
        <Probe />
      </AuthProvider>
    </QueryClientProvider>,
  );
}

describe("AuthProvider permissions", () => {
  beforeEach(() => vi.clearAllMocks());

  it("derives isAdmin and isPlatformAdmin from the profile, not the token", async () => {
    (api.getProfile as Mock).mockResolvedValue({
      email: "a@b.test",
      permissions: { manageRooms: true, manageUsers: true, managePlatform: false, manageFormulas: false },
    });

    renderProbe();

    await waitFor(() => expect(screen.getByTestId("admin").textContent).toBe("true"));
    expect(screen.getByTestId("platform").textContent).toBe("false");
    expect(screen.getByTestId("rooms").textContent).toBe("true");
    expect(screen.getByTestId("formulas").textContent).toBe("false");
  });

  it("derives canManageFormulas from the profile, not the token", async () => {
    (api.getProfile as Mock).mockResolvedValue({
      email: "a@b.test",
      permissions: { manageRooms: false, manageUsers: false, managePlatform: false, manageFormulas: true },
    });

    renderProbe();

    await waitFor(() => expect(screen.getByTestId("formulas").textContent).toBe("true"));
    expect(screen.getByTestId("admin").textContent).toBe("false");
  });

  it("grants nothing to a standard user", async () => {
    (api.getProfile as Mock).mockResolvedValue({
      email: "a@b.test",
      permissions: { manageRooms: false, manageUsers: false, managePlatform: false, manageFormulas: false },
    });

    renderProbe();

    await waitFor(() => expect(api.getProfile).toHaveBeenCalled());
    expect(screen.getByTestId("admin").textContent).toBe("false");
    expect(screen.getByTestId("platform").textContent).toBe("false");
    expect(screen.getByTestId("formulas").textContent).toBe("false");
  });

  /// Fails closed: until the profile arrives (or if it fails), the user holds no authority.
  it("grants nothing while the profile is still loading", async () => {
    (api.getProfile as Mock).mockReturnValue(new Promise(() => {}));

    renderProbe();

    expect(screen.getByTestId("admin").textContent).toBe("false");
    expect(screen.getByTestId("platform").textContent).toBe("false");
    expect(screen.getByTestId("formulas").textContent).toBe("false");
  });

  /// Signing out must drop the cached profile: otherwise the next user to sign in on this browser sees the
  /// previous user's admin menus until the refetch lands.
  it("clears the cached permissions on logout", async () => {
    (api.getProfile as Mock).mockResolvedValue({
      email: "a@b.test",
      permissions: { manageRooms: true, manageUsers: true, managePlatform: true },
    });

    function LogoutProbe() {
      const { isAdmin, logout } = useAuth();
      return (
        <>
          <span data-testid="admin">{String(isAdmin)}</span>
          <button onClick={logout}>out</button>
        </>
      );
    }

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <AuthProvider>
          <LogoutProbe />
        </AuthProvider>
      </QueryClientProvider>,
    );
    await waitFor(() => expect(screen.getByTestId("admin").textContent).toBe("true"));

    fireEvent.click(screen.getByRole("button", { name: "out" }));

    expect(qc.getQueryData(["user-profile"])).toBeUndefined();
    expect(screen.getByTestId("admin").textContent).toBe("false");
  });
});
