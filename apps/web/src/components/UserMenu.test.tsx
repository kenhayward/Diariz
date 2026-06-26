import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

const logout = vi.fn();
const setTheme = vi.fn();

vi.mock("../auth", () => ({
  useAuth: () => ({ initials: "JD", email: "jane.doe@x.com", logout }),
}));
vi.mock("../theme", () => ({
  useTheme: () => ({ theme: "auto", setTheme }),
}));
vi.mock("../lib/api", () => ({
  api: { getUserSettings: vi.fn().mockResolvedValue({ apiBase: null, model: null, hasApiKey: false }), updateUserSettings: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import UserMenu from "./UserMenu";

function renderMenu() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <UserMenu />
    </QueryClientProvider>,
  );
}

describe("UserMenu", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows the initials and opens a menu with email, Settings, themes and Sign Out", () => {
    renderMenu();
    expect(screen.getByText("JD")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    expect(screen.getByText("jane.doe@x.com")).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /settings/i })).toBeTruthy();
    expect(screen.getByRole("menuitemradio", { name: /auto/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /sign out/i })).toBeTruthy();
  });

  it("Sign Out calls logout", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /sign out/i }));
    expect(logout).toHaveBeenCalled();
  });

  it("selecting a theme calls setTheme", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    fireEvent.click(screen.getByRole("menuitemradio", { name: /dark/i }));
    expect(setTheme).toHaveBeenCalledWith("dark");
  });

  it("Settings opens the modal", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /settings/i }));
    expect(screen.getByRole("dialog", { name: /settings/i })).toBeTruthy();
  });
});
