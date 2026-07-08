import { render, screen, fireEvent, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

// useAuth is swapped per-test (mutable) so we can flip isAuthed.
let authState: { login: ReturnType<typeof vi.fn>; isAuthed: boolean } = { login: vi.fn(), isAuthed: false };
vi.mock("../auth", () => ({ useAuth: () => authState }));

// Spy on navigation while keeping the real MemoryRouter / useSearchParams / Link.
const navigateSpy = vi.fn();
vi.mock("react-router-dom", async (importActual) => {
  const actual = await importActual<typeof import("react-router-dom")>();
  return { ...actual, useNavigate: () => navigateSpy };
});

vi.mock("../lib/audioSource", () => ({ isElectron: true }));
vi.mock("../lib/api", () => ({
  api: { getAuthProviders: vi.fn().mockResolvedValue({ google: true }) },
  apiErrorMessage: (e: unknown) => String(e),
}));
vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k }),
}));

import Login from "./Login";

function renderLogin(entry = "/login") {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <Login />
    </MemoryRouter>,
  );
}

describe("Login (Electron)", () => {
  beforeEach(() => {
    navigateSpy.mockClear();
    authState = { login: vi.fn(), isAuthed: false };
    (window as unknown as { diariz: unknown }).diariz = { startGoogleSignIn: vi.fn() };
  });

  it("shows the Google button in the desktop shell and starts sign-in via IPC", async () => {
    renderLogin();
    const btn = await screen.findByRole("button", { name: /signInWithGoogle/i });
    fireEvent.click(btn);
    await waitFor(() =>
      expect((window as unknown as { diariz: { startGoogleSignIn: ReturnType<typeof vi.fn> } }).diariz.startGoogleSignIn)
        .toHaveBeenCalledOnce(),
    );
  });
});

describe("Login redirect when authenticated", () => {
  beforeEach(() => {
    navigateSpy.mockClear();
    (window as unknown as { diariz?: unknown }).diariz = undefined;
  });

  it("leaves /login as soon as auth state is true (e.g. the desktop app delivered the Google token over IPC)", () => {
    authState = { login: vi.fn(), isAuthed: true };
    renderLogin("/login");
    expect(navigateSpy).toHaveBeenCalledWith("/", { replace: true });
  });

  it("honours an internal ?returnTo= when redirecting an authed user", () => {
    authState = { login: vi.fn(), isAuthed: true };
    renderLogin("/login?returnTo=/oauth/consent");
    expect(navigateSpy).toHaveBeenCalledWith("/oauth/consent", { replace: true });
  });

  it("ignores an external returnTo (open-redirect guard)", () => {
    authState = { login: vi.fn(), isAuthed: true };
    renderLogin("/login?returnTo=//evil.example.com");
    expect(navigateSpy).toHaveBeenCalledWith("/", { replace: true });
  });

  it("does not redirect while unauthenticated", () => {
    authState = { login: vi.fn(), isAuthed: false };
    renderLogin("/login");
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});

describe("Login surfaces desktop sign-in failures", () => {
  beforeEach(() => {
    authState = { login: vi.fn(), isAuthed: false };
  });

  it("shows the reason the desktop shell reports (instead of the old silent failure)", () => {
    let handler: ((reason: string) => void) | undefined;
    (window as unknown as { diariz: unknown }).diariz = {
      startGoogleSignIn: vi.fn(),
      onAuthError: (cb: (r: string) => void) => {
        handler = cb;
        return () => {};
      },
    };
    renderLogin("/login");
    act(() => handler?.("network"));
    expect(screen.getByText("desktopSignInNetwork")).toBeTruthy();

    act(() => handler?.("expired"));
    expect(screen.getByText("desktopSignInExpired")).toBeTruthy();

    act(() => handler?.("rejected"));
    expect(screen.getByText("desktopSignInRejected")).toBeTruthy();
  });
});
