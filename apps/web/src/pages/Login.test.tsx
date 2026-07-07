import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/audioSource", () => ({ isElectron: true }));
vi.mock("../auth", () => ({ useAuth: () => ({ login: vi.fn() }) }));
vi.mock("../lib/api", () => ({
  api: { getAuthProviders: vi.fn().mockResolvedValue({ google: true }) },
  apiErrorMessage: (e: unknown) => String(e),
}));
vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k }),
}));

import Login from "./Login";

function renderLogin() {
  return render(
    <MemoryRouter>
      <Login />
    </MemoryRouter>,
  );
}

describe("Login (Electron)", () => {
  beforeEach(() => {
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
