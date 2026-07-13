import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

const logout = vi.fn();

// Mutable so individual tests can flip isAdmin / isPlatformAdmin.
const authState: {
  initials: string;
  email: string;
  fullName: string | null;
  isAdmin: boolean;
  isPlatformAdmin: boolean;
  canManageFormulas: boolean;
  logout: () => void;
} = {
  initials: "JD",
  email: "jane.doe@x.com",
  fullName: "Jane Doe",
  isAdmin: false,
  isPlatformAdmin: false,
  canManageFormulas: false,
  logout,
};

vi.mock("../auth", () => ({ useAuth: () => authState }));
vi.mock("../lib/api", () => ({
  api: {
    getPlatformSettings: vi.fn().mockResolvedValue({
      starterQuotaBytes: 5 * 1024 ** 3, maxQuotaBytes: 50 * 1024 ** 3, minutesGenerationMode: "SingleCall",
      autoDeleteAudioEnabled: false, audioRetentionDays: 30, audioDeletionTimeOfDay: "03:00:00",
      apiAccessEnabled: false, llmTimeoutSeconds: 120,
    }),
    updatePlatformSettings: vi.fn(),
    listUsers: vi.fn().mockResolvedValue([]),
    listManagedFormulas: vi.fn().mockResolvedValue([]),
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
    authState.isPlatformAdmin = false;
    authState.canManageFormulas = false;
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

  it("hides Manage Formulas for non-privileged users and shows it when canManageFormulas", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    expect(screen.queryByRole("menuitem", { name: /manage platform formulas/i })).toBeNull();

    authState.canManageFormulas = true;
    renderMenu();
    fireEvent.click(screen.getAllByRole("button", { name: /account/i })[1]);
    expect(screen.getByRole("menuitem", { name: /manage platform formulas/i })).toBeTruthy();
  });

  it("Manage Formulas opens the modal", async () => {
    authState.canManageFormulas = true;
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /manage platform formulas/i }));
    expect(await screen.findByRole("dialog", { name: /manage platform formulas/i })).toBeTruthy();
  });

  it("shows the initials and opens a menu with the user's name, Preferences and Sign Out", () => {
    renderMenu();
    expect(screen.getByText("JD")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    // The name is shown instead of the email.
    expect(screen.getByText("Jane Doe")).toBeTruthy();
    expect(screen.queryByText("jane.doe@x.com")).toBeNull();
    expect(screen.getByRole("menuitem", { name: /preferences/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /sign out/i })).toBeTruthy();
  });

  it("hides Settings for non-platform-admins and shows it for platform admins", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    expect(screen.queryByRole("menuitem", { name: /settings/i })).toBeNull();

    authState.isPlatformAdmin = true;
    renderMenu();
    fireEvent.click(screen.getAllByRole("button", { name: /account/i })[1]);
    expect(screen.getByRole("menuitem", { name: /settings/i })).toBeTruthy();
  });

  it("no longer shows the People item or the theme picker (moved into Preferences)", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    expect(screen.queryByRole("menuitem", { name: /people/i })).toBeNull();
    expect(screen.queryByRole("menuitemradio")).toBeNull();
    expect(screen.getByRole("menuitem", { name: /preferences/i })).toBeTruthy();
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

  it("renders the menu above the live-notes panel", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    // The floating live-notes panel is z-40; the account menu must sit above it, not behind.
    expect(screen.getByRole("menu").className).toContain("z-50");
  });

  it("Sign Out calls logout", () => {
    renderMenu();
    fireEvent.click(screen.getByRole("button", { name: /account/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /sign out/i }));
    expect(logout).toHaveBeenCalled();
  });

  it("Settings opens the modal for a platform admin", () => {
    authState.isPlatformAdmin = true;
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
