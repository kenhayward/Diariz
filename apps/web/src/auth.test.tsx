import { render, screen, act } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("./lib/api", () => ({
  // AuthProvider fetches the profile for the caller's permissions; it is irrelevant to these tests.
  api: { refresh: vi.fn(), getProfile: vi.fn().mockResolvedValue({ permissions: null }) },
  getToken: vi.fn(() => null),
  setToken: vi.fn(),
}));

import { AuthProvider, useAuth } from "./auth";

function Probe() {
  const { isAuthed } = useAuth();
  return <div>{isAuthed ? "authed" : "anon"}</div>;
}

describe("AuthProvider desktop token intake", () => {
  let handler: ((token: string) => void) | null = null;
  beforeEach(() => {
    handler = null;
    (window as unknown as { diariz: unknown }).diariz = {
      onAuthToken: (cb: (token: string) => void) => {
        handler = cb;
        return () => {};
      },
    };
  });

  it("adopts a token pushed by the desktop shell", () => {
    // A valid-looking JWT (header.payload.signature); payload has a future exp so it parses as authed.
    const payload = btoa(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + 3600 }));
    const token = `eyJhbGciOiJIUzI1NiJ9.${payload}.sig`;

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <AuthProvider>
          <Probe />
        </AuthProvider>
      </QueryClientProvider>,
    );
    expect(screen.getByText("anon")).toBeTruthy();

    act(() => handler!(token));
    expect(screen.getByText("authed")).toBeTruthy();
  });
});
