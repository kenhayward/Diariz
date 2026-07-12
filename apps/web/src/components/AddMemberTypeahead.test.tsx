import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import AddMemberTypeahead from "./AddMemberTypeahead";
import type { AdminUser } from "../lib/types";

const u = (over: Partial<AdminUser>): AdminUser => ({
  id: "id", email: "e@x.test", fullName: null, accountType: "Standard", status: "Active", isEnabled: true,
  quotaBytes: 0, usedBytes: 0, hasGoogle: false, ...over,
});

const users: AdminUser[] = [
  u({ id: "u1", email: "alice@x.test", fullName: "Alice Adams" }),
  u({ id: "u2", email: "bob@x.test", fullName: "Bob Barker" }),
  u({ id: "u3", email: "carol@x.test", fullName: "Carol Adams" }),
];

function setup(overrides: Partial<React.ComponentProps<typeof AddMemberTypeahead>> = {}) {
  const onAdd = vi.fn();
  render(<AddMemberTypeahead users={users} excludeIds={[]} onAdd={onAdd} {...overrides} />);
  return { onAdd, input: screen.getByRole("combobox") };
}

describe("AddMemberTypeahead", () => {
  it("shows no options until the user types", () => {
    setup();
    expect(screen.queryByRole("option")).toBeNull();
  });

  it("filters by name (case-insensitive contains)", () => {
    const { input } = setup();
    fireEvent.change(input, { target: { value: "adam" } });
    // "adam" matches Alice Adams and Carol Adams, not Bob.
    expect(screen.getByRole("option", { name: /Alice Adams/ })).toBeTruthy();
    expect(screen.getByRole("option", { name: /Carol Adams/ })).toBeTruthy();
    expect(screen.queryByRole("option", { name: /Bob Barker/ })).toBeNull();
  });

  it("filters by email as well as name", () => {
    const { input } = setup();
    fireEvent.change(input, { target: { value: "bob@" } });
    expect(screen.getByRole("option", { name: /Bob Barker/ })).toBeTruthy();
    expect(screen.queryByRole("option", { name: /Alice Adams/ })).toBeNull();
  });

  it("excludes users already in the group", () => {
    const { input } = setup({ excludeIds: ["u1"] });
    fireEvent.change(input, { target: { value: "adam" } });
    // Alice (u1) is excluded; Carol (u3) still matches.
    expect(screen.queryByRole("option", { name: /Alice Adams/ })).toBeNull();
    expect(screen.getByRole("option", { name: /Carol Adams/ })).toBeTruthy();
  });

  it("calls onAdd with the chosen user id and clears the input", () => {
    const { onAdd, input } = setup();
    fireEvent.change(input, { target: { value: "bob" } });
    fireEvent.click(screen.getByRole("option", { name: /Bob Barker/ }));
    expect(onAdd).toHaveBeenCalledWith("u2");
    expect((screen.getByRole("combobox") as HTMLInputElement).value).toBe("");
    expect(screen.queryByRole("option")).toBeNull();
  });

  it("closes the results on Escape", () => {
    const { input } = setup();
    fireEvent.change(input, { target: { value: "adam" } });
    expect(screen.getByRole("option", { name: /Alice Adams/ })).toBeTruthy();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("option")).toBeNull();
  });
});
