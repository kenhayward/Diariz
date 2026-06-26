import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../auth", () => ({ useAuth: () => ({ email: "me@x.test" }) }));
vi.mock("../lib/api", () => ({
  api: {
    listUsers: vi.fn(),
    grantUser: vi.fn(),
    denyUser: vi.fn(),
    setUserRole: vi.fn(),
    setUserEnabled: vi.fn(),
    deleteUser: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import ManageUsersModal from "./ManageUsersModal";
import type { AdminUser } from "../lib/types";

const u = (over: Partial<AdminUser>): AdminUser => ({
  id: "id", email: "e@x.test", fullName: null, accountType: "Standard", status: "Active", isEnabled: true, ...over,
});

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;
const render_ = () => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><ManageUsersModal onClose={() => {}} /></QueryClientProvider>);
};

describe("ManageUsersModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("grants a request and shows the fallback link when email is unconfigured", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "req1", email: "want@x.test", status: "Requested" })]);
    mock(api.grantUser).mockResolvedValue({ emailed: false, setupUrl: "http://x/setup?email=want&token=abc" });
    render_();

    fireEvent.click(await screen.findByRole("button", { name: /grant/i }));

    await waitFor(() => expect(api.grantUser).toHaveBeenCalledWith("req1"));
    expect(await screen.findByText(/setup\?email=want&token=abc/)).toBeTruthy();
  });

  it("promotes a standard user", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test", accountType: "Standard" })]);
    render_();
    fireEvent.click(await screen.findByRole("button", { name: /make admin/i }));
    await waitFor(() => expect(api.setUserRole).toHaveBeenCalledWith("s1", "Administrator"));
  });

  it("deletes a user after confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    render_();
    fireEvent.click(await screen.findByRole("button", { name: /delete/i }));
    await waitFor(() => expect(api.deleteUser).toHaveBeenCalledWith("s1"));
  });

  it("protects the Platform Administrator and the current user from destructive actions", async () => {
    mock(api.listUsers).mockResolvedValue([
      u({ id: "plat", email: "plat@x.test", accountType: "PlatformAdministrator" }),
      u({ id: "self", email: "me@x.test" }),
      u({ id: "other", email: "other@x.test" }),
    ]);
    render_();
    await screen.findByText("plat@x.test");

    const platRow = screen.getByText("plat@x.test").closest("li")!;
    const selfRow = screen.getByText("me@x.test").closest("li")!;
    const otherRow = screen.getByText("other@x.test").closest("li")!;

    expect(within(platRow).queryByRole("button", { name: /delete/i })).toBeNull();
    expect(within(selfRow).queryByRole("button", { name: /delete/i })).toBeNull();
    expect(within(otherRow).getByRole("button", { name: /delete/i })).toBeTruthy();
  });
});
