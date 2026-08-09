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
import type { AdminUser, Group } from "../lib/types";

const u = (over: Partial<AdminUser>): AdminUser => ({
  id: "id", email: "e@x.test", fullName: null, accountType: "Standard", status: "Active", isEnabled: true,
  quotaBytes: 5 * 1024 ** 3, usedBytes: 0, hasGoogle: false, pictureUrl: null, ...over,
});

const g = (over: Partial<Group> & Pick<Group, "id" | "name">): Group => ({
  description: null, icon: null, color: null, permissions: 0, isSystem: false, memberIds: [], ...over,
});

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;
const render_ = (onClose: () => void = () => {}) => {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}><ManageUsersModal onClose={onClose} /></QueryClientProvider>);
};

/// Selecting a row is the gateway to the whole detail pane, so nearly every test below starts here. Rows are
/// buttons whose accessible name is "name, email, status".
const selectUser = async (email: string) =>
  fireEvent.click(await screen.findByRole("button", { name: new RegExp(email) }));

const openTab = async (name: RegExp) => fireEvent.click(await screen.findByRole("tab", { name }));

describe("ManageUsersModal", () => {
  beforeEach(() => vi.clearAllMocks());

  // ---- Shell ----

  it("does not close on a backdrop click, but does on Escape and on the close button", async () => {
    mock(api.listUsers).mockResolvedValue([]);
    const onClose = vi.fn();
    const { container } = render_(onClose);
    await screen.findByRole("dialog");

    fireEvent.click(container.firstChild as Element);
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole("button", { name: /close/i }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it("badges the Requests tab with the pending count, and drops the badge when there are none", async () => {
    mock(api.listUsers).mockResolvedValue([
      u({ id: "r1", email: "a@x.test", status: "Requested" }),
      u({ id: "r2", email: "b@x.test", status: "Requested" }),
      u({ id: "s1", email: "std@x.test" }),
    ]);
    const { unmount } = render_();
    expect(await screen.findByRole("tab", { name: /requests\s*2/i })).toBeTruthy();
    unmount();

    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    render_();
    const tab = await screen.findByRole("tab", { name: /requests/i });
    expect(tab.textContent).toBe("Requests");
  });

  // ---- Users tab ----

  it("keeps pending requests out of the user list, and shows them on Requests", async () => {
    mock(api.listUsers).mockResolvedValue([
      u({ id: "s1", email: "std@x.test" }),
      u({ id: "r1", email: "want@x.test", status: "Requested" }),
    ]);
    render_();

    await screen.findByRole("button", { name: /std@x.test/ });
    expect(screen.queryByRole("button", { name: /want@x.test/ })).toBeNull();

    await openTab(/requests/i);
    expect(await screen.findByText("want@x.test")).toBeTruthy();
  });

  it("filters the list as you type, over name and email alike", async () => {
    mock(api.listUsers).mockResolvedValue([
      u({ id: "1", email: "priya@x.test", fullName: "Priya Shah" }),
      u({ id: "2", email: "tom@x.test", fullName: "Tom Okafor" }),
    ]);
    render_();
    const box = await screen.findByLabelText(/search name or email/i);

    fireEvent.change(box, { target: { value: "okafor" } });
    expect(screen.queryByRole("button", { name: /priya@x.test/ })).toBeNull();
    expect(screen.getByRole("button", { name: /tom@x.test/ })).toBeTruthy();

    fireEvent.change(box, { target: { value: "priya@" } });
    expect(screen.getByRole("button", { name: /priya@x.test/ })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /tom@x.test/ })).toBeNull();
  });

  it("counts each status on its chip and narrows to it when pressed", async () => {
    mock(api.listUsers).mockResolvedValue([
      u({ id: "1", email: "act@x.test" }),
      u({ id: "2", email: "inv@x.test", status: "Invited" }),
      u({ id: "3", email: "off@x.test", isEnabled: false }),
      u({ id: "4", email: "req@x.test", status: "Requested" }),
    ]);
    render_();

    // Requested is excluded from All, so the total agrees with the rows on screen.
    const all = await screen.findByRole("button", { name: "All 3" });
    expect(all.getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("button", { name: "Awaiting setup 1" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Disabled 1" }));
    expect(screen.getByRole("button", { name: "Disabled 1" }).getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("button", { name: /off@x.test/ })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /act@x.test/ })).toBeNull();
  });

  it("adds a user and shows the fallback setup link when email is unconfigured", async () => {
    mock(api.listUsers).mockResolvedValue([]);
    mock(api.addUser).mockResolvedValue({ emailed: false, setupUrl: "http://x/setup?email=new&token=t1" });
    render_();

    fireEvent.change(await screen.findByLabelText(/new user email/i), { target: { value: "new@x.test" } });
    fireEvent.click(screen.getByRole("button", { name: /add user/i }));

    await waitFor(() => expect(api.addUser).toHaveBeenCalledWith("new@x.test", undefined));
    expect(await screen.findByText(/setup\?email=new&token=t1/)).toBeTruthy();
  });

  it("shows a Google badge on a linked account", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test", hasGoogle: true })]);
    render_();

    await selectUser("std@x.test");
    expect(screen.getAllByText("Google")).toHaveLength(1);
  });

  it("has no Make Admin control - authority comes from group membership", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    render_();

    await selectUser("std@x.test");
    expect(screen.queryByRole("button", { name: /make admin/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /make standard/i })).toBeNull();
  });

  // ---- The reason the detail pane exists ----

  it("says in plain language what the selected user's groups let them do", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test", fullName: "Std User" })]);
    mock(api.listGroups).mockResolvedValue([
      g({ id: "g1", name: "Admins", permissions: 1, memberIds: ["s1"] }),      // manage rooms
      g({ id: "g2", name: "Research", permissions: 8 | 16, memberIds: ["s1"] }), // formulas + people
      g({ id: "g3", name: "Elsewhere", permissions: 4, memberIds: ["other"] }), // not theirs
    ]);
    render_();

    await selectUser("std@x.test");
    const line = await screen.findByText(/^Grants:/);
    expect(line.textContent).toBe("Grants: manage rooms, manage formulas, manage the People directory.");
    // The group they are not in must not leak into the sentence.
    expect(line.textContent).not.toContain("platform");
  });

  it("says so when a user's groups grant nothing", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    mock(api.listGroups).mockResolvedValue([g({ id: "g1", name: "Everyone", permissions: 0, memberIds: ["s1"] })]);
    render_();

    await selectUser("std@x.test");
    expect(await screen.findByText("No platform permissions.")).toBeTruthy();
  });

  it("lists the user's groups in the row and lets the pane remove one", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    mock(api.listGroups).mockResolvedValue([g({ id: "g1", name: "Administrators", permissions: 2, memberIds: ["s1"] })]);
    mock(api.removeGroupMember).mockResolvedValue(undefined);
    render_();

    await selectUser("std@x.test");
    fireEvent.click(await screen.findByRole("button", { name: /remove std@x.test from Administrators/i }));
    await waitFor(() => expect(api.removeGroupMember).toHaveBeenCalledWith("g1", "s1"));
  });

  // ---- Quota, disable, delete ----

  it("saves a quota in GB and refreshes the account menu's storage figure", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    mock(api.setUserQuota).mockResolvedValue(undefined);
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const spy = vi.spyOn(qc, "invalidateQueries");
    render(<QueryClientProvider client={qc}><ManageUsersModal onClose={() => {}} /></QueryClientProvider>);

    await selectUser("std@x.test");
    fireEvent.change(screen.getByLabelText(/quota for std@x.test/i), { target: { value: "10" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.setUserQuota).toHaveBeenCalledWith("s1", 10 * 1024 ** 3));
    await waitFor(() => expect(spy).toHaveBeenCalledWith({ queryKey: ["user-storage"] }));
  });

  it("deletes an account only after the confirm", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    mock(api.deleteUser).mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render_();

    await selectUser("std@x.test");
    fireEvent.click(await screen.findByRole("button", { name: /delete account/i }));
    await waitFor(() => expect(api.deleteUser).toHaveBeenCalledWith("s1"));
  });

  /// The server refuses both, and the old table simply rendered nothing where the buttons would be - which
  /// read as a bug. The pane now says which rule applies.
  it("offers no Disable or Delete for the Platform Administrator or yourself, and says why", async () => {
    mock(api.listUsers).mockResolvedValue([
      u({ id: "p1", email: "plat@x.test", accountType: "PlatformAdministrator" }),
      u({ id: "m1", email: "me@x.test" }),
      u({ id: "s1", email: "std@x.test" }),
    ]);
    render_();

    await selectUser("plat@x.test");
    expect(await screen.findByText(/Platform Administrator can't be disabled or deleted/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /delete account/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /^disable$/i })).toBeNull();

    await selectUser("me@x.test");
    expect(await screen.findByText(/can't disable or delete your own account/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /delete account/i })).toBeNull();

    await selectUser("std@x.test");
    expect(await screen.findByRole("button", { name: /delete account/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /^disable$/i })).toBeTruthy();
  });

  // ---- Requests tab ----

  it("grants a request and shows the fallback link when email is unconfigured", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "req1", email: "want@x.test", status: "Requested" })]);
    mock(api.grantUser).mockResolvedValue({ emailed: false, setupUrl: "http://x/setup?email=want&token=abc" });
    render_();

    await openTab(/requests/i);
    fireEvent.click(await screen.findByRole("button", { name: /^grant$/i }));

    await waitFor(() => expect(api.grantUser).toHaveBeenCalledWith("req1"));
    expect(await screen.findByText(/setup\?email=want&token=abc/)).toBeTruthy();
  });

  it("denies a request only after the confirm", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "req1", email: "want@x.test", status: "Requested" })]);
    mock(api.denyUser).mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render_();

    await openTab(/requests/i);
    fireEvent.click(await screen.findByRole("button", { name: /^deny$/i }));
    await waitFor(() => expect(api.denyUser).toHaveBeenCalledWith("req1"));
  });

  it("says so when nothing is pending", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    render_();

    await openTab(/requests/i);
    expect(await screen.findByText("No pending requests.")).toBeTruthy();
  });

  // ---- Tab switching ----

  it("swaps the whole body when the tab changes", async () => {
    mock(api.listUsers).mockResolvedValue([u({ id: "s1", email: "std@x.test" })]);
    render_();
    await screen.findByRole("button", { name: /std@x.test/ });

    await openTab(/groups/i);
    expect(screen.getByTestId("new-group-form")).toBeTruthy();
    expect(screen.queryByLabelText(/search name or email/i)).toBeNull();
  });
});

describe("ManageUsersModal groups tab", () => {
  beforeEach(() => vi.clearAllMocks());

  const systemGroup = g({ id: "g1", name: "Platform Administrators", permissions: 7, isSystem: true, memberIds: ["u1"] });
  const ordinary = g({ id: "g2", name: "Engineering", description: "Day-to-day admins.", permissions: 1, memberIds: ["u2"] });
  const users = [
    u({ id: "u1", email: "plat@x.test", fullName: "Plat Admin" }),
    u({ id: "u2", email: "eng@x.test", fullName: "Eng Person" }),
  ];

  const openGroups = async (groups = [systemGroup, ordinary]) => {
    mock(api.listGroups).mockResolvedValue(groups);
    mock(api.listUsers).mockResolvedValue(users);
    render_();
    await openTab(/groups/i);
  };

  const selectGroup = async (name: RegExp) => fireEvent.click(await screen.findByRole("button", { name }));

  it("lists every group with its permission and member counts", async () => {
    await openGroups();
    expect(await screen.findByText("Platform Administrators")).toBeTruthy();
    expect(screen.getByText("3 permissions · 1 member")).toBeTruthy();
    expect(screen.getByText("1 permission · 1 member")).toBeTruthy();
  });

  it("shows nothing selected until a group is picked", async () => {
    await openGroups();
    expect(await screen.findByText(/select a group on the left/i)).toBeTruthy();
  });

  /// A column headed MANAGE PLATFORM over a bare checkbox never said what it would let someone do.
  it("gives every permission a sentence, not just a name", async () => {
    await openGroups();
    await selectGroup(/Engineering/);
    expect(await screen.findByText("Platform-wide settings, model defaults, integrations, backup and restore.")).toBeTruthy();
    expect(screen.getByText("Create shared rooms, and add or remove their members.")).toBeTruthy();
  });

  it("toggles a permission, sending the whole group shape", async () => {
    mock(api.updateGroup).mockResolvedValue(undefined);
    await openGroups();
    await selectGroup(/Engineering/);

    fireEvent.click(await screen.findByTestId("perm-g2-2"));
    await waitFor(() =>
      expect(api.updateGroup).toHaveBeenCalledWith("g2", expect.objectContaining({ permissions: 3, name: "Engineering" })),
    );
  });

  it("locks the system group's permissions and offers it no Edit or Delete", async () => {
    await openGroups();
    await selectGroup(/Platform Administrators/);

    expect((await screen.findByTestId("perm-g1-4") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByTestId("perm-g1-4") as HTMLInputElement).checked).toBe(true);
    expect(screen.queryByTestId("delete-group-g1")).toBeNull();
    expect(screen.queryByTestId("edit-group-g1")).toBeNull();
    expect(screen.getByText(/this group is required/i)).toBeTruthy();
  });

  it("deletes a group after the confirm", async () => {
    mock(api.deleteGroup).mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    await openGroups();
    await selectGroup(/Engineering/);

    fireEvent.click(await screen.findByTestId("delete-group-g2"));
    await waitFor(() => expect(api.deleteGroup).toHaveBeenCalledWith("g2"));
  });

  it("creates a group and selects it", async () => {
    mock(api.createGroup).mockResolvedValue(g({ id: "g3", name: "Support" }));
    await openGroups();

    fireEvent.change(await screen.findByLabelText(/new group name/i), { target: { value: "Support" } });
    fireEvent.submit(screen.getByTestId("new-group-form"));

    await waitFor(() => expect(api.createGroup).toHaveBeenCalledWith(expect.objectContaining({ name: "Support" })));
  });

  it("shows members as chips and removes one in place", async () => {
    mock(api.removeGroupMember).mockResolvedValue(undefined);
    await openGroups();
    await selectGroup(/Engineering/);

    expect(await screen.findByTestId("member-g2-u2")).toBeTruthy();
    expect(screen.queryByTestId("member-g2-u1")).toBeNull();

    fireEvent.click(screen.getByTestId("member-g2-u2"));
    await waitFor(() => expect(api.removeGroupMember).toHaveBeenCalledWith("g2", "u2"));
  });

  it("adds a member through the picker", async () => {
    mock(api.addGroupMember).mockResolvedValue(undefined);
    await openGroups();
    await selectGroup(/Engineering/);

    fireEvent.change(await screen.findByRole("combobox"), { target: { value: "plat" } });
    fireEvent.click(await screen.findByRole("option", { name: /Plat Admin/ }));
    await waitFor(() => expect(api.addGroupMember).toHaveBeenCalledWith("g2", "u1"));
  });

  it("will not let the system group lose its last member", async () => {
    await openGroups();
    await selectGroup(/Platform Administrators/);
    expect(((await screen.findByTestId("member-g1-u1")) as HTMLButtonElement).disabled).toBe(true);
  });

  /// description, icon and colour have been on the Group type all along, and no screen has ever set them.
  it("edits a group's name, description and colour in one dialog", async () => {
    mock(api.updateGroup).mockResolvedValue(undefined);
    await openGroups();
    await selectGroup(/Engineering/);

    fireEvent.click(await screen.findByTestId("edit-group-g2"));
    const dialog = within(await screen.findByRole("dialog", { name: /edit group/i }));
    fireEvent.change(dialog.getByLabelText(/^name$/i), { target: { value: "Engineering EMEA" } });
    fireEvent.change(dialog.getByLabelText(/^description$/i), { target: { value: "Regional admins." } });
    fireEvent.click(dialog.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateGroup).toHaveBeenCalledWith("g2", {
        name: "Engineering EMEA",
        description: "Regional admins.",
        icon: null,
        color: "#9ca3af",
        // Untouched: saving a name must never strip the group's rights.
        permissions: 1,
      }),
    );
  });

  it("shows a group's description under its name", async () => {
    await openGroups();
    await selectGroup(/Engineering/);
    expect(await screen.findByText("Day-to-day admins.")).toBeTruthy();
  });
});
