import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listGroups: vi.fn(),
    listUsers: vi.fn(),
    addGroupMember: vi.fn().mockResolvedValue(undefined),
    removeGroupMember: vi.fn().mockResolvedValue(undefined),
  },
}));

import { api } from "../lib/api";
import GroupMembersModal from "./GroupMembersModal";
import type { Group } from "../lib/types";

const group: Group = {
  id: "g2", name: "Engineering", description: null, icon: null, color: null,
  permissions: 1, isSystem: false, memberIds: ["u1"],
};
const users = [
  { id: "u1", email: "alice@x.test", fullName: "Alice Adams" },
  { id: "u2", email: "bob@x.test", fullName: "Bob Barker" },
];

function renderModal(onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <GroupMembersModal group={group} onClose={onClose} />
    </QueryClientProvider>,
  );
  return { onClose };
}

describe("GroupMembersModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listGroups as Mock).mockResolvedValue([group]);
    (api.listUsers as Mock).mockResolvedValue(users);
  });

  it("lists the group's current members with a Remove control", async () => {
    renderModal();
    expect(await screen.findByText("Alice Adams")).toBeTruthy();
    expect(screen.getByTestId("member-g2-u1")).toBeTruthy();
    // A non-member is not listed among current members.
    expect(screen.queryByText("Bob Barker")).toBeNull();
  });

  it("removes a member", async () => {
    renderModal();
    fireEvent.click(await screen.findByTestId("member-g2-u1"));
    await waitFor(() => expect(api.removeGroupMember).toHaveBeenCalledWith("g2", "u1"));
  });

  it("adds a member via the type-ahead (matches by name, excludes current members)", async () => {
    renderModal();
    await screen.findByText("Alice Adams"); // wait for the user list to load
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "bob" } });
    fireEvent.click(screen.getByRole("option", { name: /Bob Barker/ }));
    await waitFor(() => expect(api.addGroupMember).toHaveBeenCalledWith("g2", "u2"));
  });

  it("does not offer a current member in the add type-ahead", async () => {
    renderModal();
    await screen.findByText("Alice Adams"); // wait for the user list to load
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "alice" } });
    expect(screen.queryByRole("option", { name: /Alice Adams/ })).toBeNull();
  });

  it("closes on the Close button and on Escape, but not on a backdrop click", async () => {
    const { onClose } = renderModal();
    await screen.findByText("Alice Adams");

    // Backdrop click does not close.
    fireEvent.click(screen.getByTestId("group-members-backdrop"));
    expect(onClose).not.toHaveBeenCalled();

    // Escape closes.
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);

    // Close button closes.
    fireEvent.click(screen.getByRole("button", { name: /close/i }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
