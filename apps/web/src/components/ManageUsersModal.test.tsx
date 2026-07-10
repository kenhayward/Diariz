import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../auth", () => ({ useAuth: () => ({ email: "me@x.test" }) }));
vi.mock("../lib/api", () => ({
  api: {
    listUsers: vi.fn(),
    addUser: vi.fn(),
    grantUser: vi.fn(),
    denyUser: vi.fn(),
    setUserRole: vi.fn(),
    setUserEnabled: vi.fn(),
    setUserQuota: vi.fn(),
    deleteUser: vi.fn(),
    getPlatformSettings: vi.fn().mockResolvedValue({ starterQuotaBytes: 5 * 1024 ** 3, maxQuotaBytes: 50 * 1024 ** 3 }),
    listGroups: vi.fn().mockResolvedValue([]),
    createGroup: vi.fn(),
    updateGroup: vi.fn(),
    deleteGroup: vi.fn(),
    addGroupMember: vi.fn(),
    removeGroupMember: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import ManageUsersModal from "./ManageUsersModal";
import type { AdminUser } from "../lib/types";

const u = (over: Partial<AdminUser>): AdminUser => ({
  id: "id", email: "e@x.test", fullName: null, accountType: "Standard", status: "Active", isEnabled: true,
  quotaBytes: 5 * 1024 ** 3, usedBytes: 0, hasGoogle: false, ...over,
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

  it("adds a user by email and shows the fallback link when email is unconfigured", async () => {
    mock(api.listUsers).mockResolvedValue([]);
    mock(api.addUser).mockResolvedValue({ emailed: false, setupUrl: "http://x/setup?email=new&token=abc" });
    render_();

    fireEvent.change(await screen.findByLabelText(/new user email/i), { target: { value: "new@x.test" } });
    fireEvent.click(screen.getByRole("button", { name: /add user/i }));

    await waitFor(() => expect(api.addUser).toHaveBeenCalledWith("new@x.test", undefined));
    expect(await screen.findByText(/setup\?email=new&token=abc/)).toBeTruthy();
  });

  it("shows an onboarding status pill for invited users", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "i1", email: "inv@x.test", status: "Invited" })]);
    render_();
    expect(await screen.findByText(/awaiting setup/i)).toBeTruthy();
  });

  it("shows a Google badge only for Google-linked users", async () => {
    mock(api.listUsers).mockResolvedValue([
      u({ id: "g1", email: "goog@x.test", hasGoogle: true }),
      u({ id: "p1", email: "pass@x.test", hasGoogle: false }),
    ]);
    render_();
    await screen.findByText("goog@x.test");
    expect(screen.getAllByText("Google")).toHaveLength(1); // exactly the one linked user
  });

  it("promotes a standard user", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test", accountType: "Standard" })]);
    render_();
    fireEvent.click(await screen.findByRole("button", { name: /make admin/i }));
    await waitFor(() => expect(api.setUserRole).toHaveBeenCalledWith("s1", "Administrator"));
  });

  it("edits a user's storage quota (GB → bytes)", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test", quotaBytes: 5 * 1024 ** 3 })]);
    mock(api.setUserQuota).mockResolvedValue(undefined);
    render_();

    fireEvent.click(await screen.findByRole("button", { name: /edit quota/i }));
    const input = screen.getByLabelText(/quota for std@x.test/i);
    fireEvent.change(input, { target: { value: "10" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.setUserQuota).toHaveBeenCalledWith("s1", 10 * 1024 ** 3));
  });

  it("refreshes the account storage query after a quota change", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test", quotaBytes: 5 * 1024 ** 3 })]);
    mock(api.setUserQuota).mockResolvedValue(undefined);
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const spy = vi.spyOn(qc, "invalidateQueries");
    render(<QueryClientProvider client={qc}><ManageUsersModal onClose={() => {}} /></QueryClientProvider>);

    fireEvent.click(await screen.findByRole("button", { name: /edit quota/i }));
    fireEvent.change(screen.getByLabelText(/quota for std@x.test/i), { target: { value: "10" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    // The account dropdown reads ["user-storage"]; a quota change must invalidate it so the header updates.
    await waitFor(() => expect(spy).toHaveBeenCalledWith({ queryKey: ["user-storage"] }));
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

    const platRow = screen.getByText("plat@x.test").closest("tr")!;
    const selfRow = screen.getByText("me@x.test").closest("tr")!;
    const otherRow = screen.getByText("other@x.test").closest("tr")!;

    expect(within(platRow).queryByRole("button", { name: /delete/i })).toBeNull();
    expect(within(selfRow).queryByRole("button", { name: /delete/i })).toBeNull();
    expect(within(otherRow).getByRole("button", { name: /delete/i })).toBeTruthy();
  });

  it("renders the users as a table with a column header row", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "a", email: "a@x.test" })]);
    render_();
    await screen.findByText("a@x.test");
    expect(screen.getByRole("columnheader", { name: /user/i })).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: /storage/i })).toBeTruthy();
  });
});

describe("ManageUsersModal groups", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows each user's groups instead of an account type", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "u1", email: "a@x.test", accountType: "Administrator" })]);
    mock(api.listGroups).mockResolvedValue([
      { id: "g1", name: "Administrators", description: null, icon: null, color: null, permissions: 3, isSystem: false, memberIds: ["u1"] },
      { id: "g2", name: "Engineering", description: null, icon: null, color: null, permissions: 0, isSystem: false, memberIds: [] },
    ]);

    render_();

    expect(await screen.findByText("Administrators")).toBeTruthy();
    // The raw account type must no longer be rendered: group membership is the truth now.
    expect(screen.queryByText("Administrator")).toBeNull();
    expect(screen.queryByText("Engineering")).toBeNull(); // not a member
  });

  it("switches to the Groups tab", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "u1", email: "a@x.test" })]);
    mock(api.listGroups).mockResolvedValue([
      { id: "g2", name: "Engineering", description: null, icon: null, color: null, permissions: 1, isSystem: false, memberIds: [] },
    ]);

    render_();
    fireEvent.click(await screen.findByRole("tab", { name: /groups/i }));

    await waitFor(() => expect(screen.getByTestId("new-group-form")).toBeTruthy());
    // The users table is gone while the Groups tab is open.
    expect(screen.queryByText("a@x.test")).toBeNull();
  });
});
