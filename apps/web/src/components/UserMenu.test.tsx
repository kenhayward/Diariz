import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

const logout = vi.fn();
const setTheme = vi.fn();

// Mutable so individual tests can flip isAdmin.
const authState: { initials: string; email: string; fullName: string | null; isAdmin: boolean; logout: () => void } = {
  initials: "JD", email: "jane.doe@x.com", fullName: "Jane Doe", isAdmin: false, logout,
};

vi.mock("../auth", () => ({ useAuth: () => authState }));
vi.mock("../theme", () => ({
  useTheme: () => ({ theme: "auto", setTheme }),
}));
vi.mock("../lib/api", () => ({
  api: {
    getUserSettings: vi.fn().mockResolvedValue({ apiBase: null, model: null, hasApiKey: false }),
    updateUserSettings: vi.fn(),
    listUsers: vi.fn().mockResolvedValue([]),
    getUserStorage: vi.fn().mockResolvedValue({ usedBytes: 1024 ** 3, quotaBytes: 5 * 1024 ** 3, totalTranscriptionMs: 3_661_000 }),
  },
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
  beforeEach(() => {
    vi.clearAllMocks();
    authState.isAdmin = false;
  });

  it("hides Manage Users for non-admins and shows it for admins", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    expect(screen.queryByRole("menuitem", { name: /manage users/i })).toBeNull();

    authState.isAdmin = true;
    renderMenu();
    fireEvent.click(screen.getAllByRole("button", { name: /account/i })[1]);
    expect(screen.getByRole("menuitem", { name: /manage users/i })).toBeTruthy();
  });

  it("shows the initials and opens a menu with the user's name, Settings, themes and Sign Out", () => {
    renderMenu();
    expect(screen.getByText("JD")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    // The name is shown instead of the email.
    expect(screen.getByText("Jane Doe")).toBeTruthy();
    expect(screen.queryByText("jane.doe@x.com")).toBeNull();
    expect(screen.getByRole("menuitem", { name: /settings/i })).toBeTruthy();
    expect(screen.getByRole("menuitemradio", { name: /auto/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /sign out/i })).toBeTruthy();
  });

  it("shows the storage usage line", async () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    // 1 GB used of 5 GB = 20%.
    expect(await screen.findByText(/Storage 1 GB \/ 5 GB \(20%\)/)).toBeTruthy();
  });

  it("shows the total transcription time below the storage line", async () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    // 3,661,000 ms = 1:01:01, no day part.
    expect(await screen.findByText(/Transcription 1:01:01/)).toBeTruthy();
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

  it("About opens the about box", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /^about$/i }));
    expect(screen.getByRole("dialog", { name: /about diariz/i })).toBeTruthy();
  });
});
