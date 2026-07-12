import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listGroups: vi.fn(),
    createGroup: vi.fn().mockResolvedValue({}),
    updateGroup: vi.fn().mockResolvedValue(undefined),
    deleteGroup: vi.fn().mockResolvedValue(undefined),
    addGroupMember: vi.fn().mockResolvedValue(undefined),
    removeGroupMember: vi.fn().mockResolvedValue(undefined),
    listUsers: vi.fn(),
  },
}));

import { api } from "../lib/api";
import GroupsTab from "./GroupsTab";

// permissions bits: 1 = rooms, 2 = users, 4 = platform
const systemGroup = {
  id: "g1", name: "Platform Administrators", description: null, icon: null, color: null,
  permissions: 7, isSystem: true, memberIds: ["u1"],
};
const ordinary = {
  id: "g2", name: "Engineering", description: null, icon: null, color: null,
  permissions: 1, isSystem: false, memberIds: [] as string[],
};
const users = [
  { id: "u1", email: "plat@x.test", fullName: "Plat Admin" },
  { id: "u2", email: "dev@x.test", fullName: "Dev Person" },
];

function renderTab() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <GroupsTab />
    </QueryClientProvider>,
  );
}

describe("GroupsTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listGroups as Mock).mockResolvedValue([systemGroup, ordinary]);
    (api.listUsers as Mock).mockResolvedValue(users);
  });

  it("lists groups", async () => {
    renderTab();
    expect(await screen.findByText("Platform Administrators")).toBeTruthy();
    expect(screen.getByText("Engineering")).toBeTruthy();
  });

  it("offers no Delete for the system group", async () => {
    renderTab();
    await screen.findByText("Platform Administrators");
    expect(screen.queryByTestId("delete-group-g1")).toBeNull();
    expect(screen.getByTestId("delete-group-g2")).toBeTruthy();
  });

  it("deletes an ordinary group after confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    renderTab();
    await screen.findByText("Engineering");

    fireEvent.click(screen.getByTestId("delete-group-g2"));

    await waitFor(() => expect(api.deleteGroup).toHaveBeenCalledWith("g2"));
  });

  it("locks the system group's permission checkboxes", async () => {
    renderTab();
    await screen.findByText("Platform Administrators");

    expect((screen.getByTestId("perm-g1-4") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByTestId("perm-g1-4") as HTMLInputElement).checked).toBe(true);
    expect((screen.getByTestId("perm-g2-1") as HTMLInputElement).disabled).toBe(false);
  });

  it("toggles a permission on an ordinary group", async () => {
    renderTab();
    await screen.findByText("Engineering");

    fireEvent.click(screen.getByTestId("perm-g2-2")); // grant ManageUsers (bit 2) on top of rooms (1)

    await waitFor(() =>
      expect(api.updateGroup).toHaveBeenCalledWith("g2", expect.objectContaining({ permissions: 3 })),
    );
  });

  it("creates a group", async () => {
    renderTab();
    await screen.findByText("Engineering");

    fireEvent.change(screen.getByLabelText(/new group/i), { target: { value: "Support" } });
    fireEvent.submit(screen.getByTestId("new-group-form"));

    await waitFor(() => expect(api.createGroup).toHaveBeenCalledWith(expect.objectContaining({ name: "Support" })));
  });

  it("opens the members dialog to add and remove members", async () => {
    // Engineering (g2) starts with Dev Person (u2) as a member for this test.
    (api.listGroups as Mock).mockResolvedValue([systemGroup, { ...ordinary, memberIds: ["u2"] }]);
    renderTab();

    // The member-count button opens the per-group members dialog (not an inline list of every user).
    fireEvent.click(await screen.findByTestId("members-g2"));

    // Remove the current member.
    fireEvent.click(await screen.findByTestId("member-g2-u2"));
    await waitFor(() => expect(api.removeGroupMember).toHaveBeenCalledWith("g2", "u2"));

    // Add another user via the type-ahead (excludes current members; matches by name).
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "plat" } });
    fireEvent.click(await screen.findByRole("option", { name: /Plat Admin/ }));
    await waitFor(() => expect(api.addGroupMember).toHaveBeenCalledWith("g2", "u1"));
  });
});
