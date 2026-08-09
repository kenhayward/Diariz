import { describe, it, expect } from "vitest";
import type { AdminUser, Group } from "../../lib/types";
import { bucketOf, countByStatus, effectivePermissionBits, filterUsers } from "./userFilters";

function u(over: Partial<AdminUser> & Pick<AdminUser, "id" | "email">): AdminUser {
  return {
    fullName: null,
    accountType: "Standard",
    status: "Active",
    isEnabled: true,
    quotaBytes: 5 * 1024 ** 3,
    usedBytes: 0,
    hasGoogle: false,
    pictureUrl: null,
    ...over,
  };
}

function g(over: Partial<Group> & Pick<Group, "id" | "name">): Group {
  return {
    description: null,
    icon: null,
    color: null,
    permissions: 0,
    isSystem: false,
    memberIds: [],
    ...over,
  };
}

describe("bucketOf", () => {
  it("files an active, enabled account under active", () => {
    expect(bucketOf(u({ id: "1", email: "a@x.test" }))).toBe("active");
  });

  it("files an invited account under invited", () => {
    expect(bucketOf(u({ id: "1", email: "a@x.test", status: "Invited" }))).toBe("invited");
  });

  /// Order matters: an account can be both Active and switched off, and it must be counted once. Disabled
  /// is the fact an administrator is looking for, so it wins.
  it("files a disabled account under disabled whatever its status says", () => {
    expect(bucketOf(u({ id: "1", email: "a@x.test", status: "Active", isEnabled: false }))).toBe("disabled");
    expect(bucketOf(u({ id: "2", email: "b@x.test", status: "Invited", isEnabled: false }))).toBe("disabled");
  });

  it("returns null for a pending request - those live on the Requests tab", () => {
    expect(bucketOf(u({ id: "1", email: "a@x.test", status: "Requested" }))).toBeNull();
  });
});

describe("countByStatus", () => {
  const users = [
    u({ id: "1", email: "a@x.test" }),
    u({ id: "2", email: "b@x.test" }),
    u({ id: "3", email: "c@x.test", status: "Invited" }),
    u({ id: "4", email: "d@x.test", isEnabled: false }),
    u({ id: "5", email: "e@x.test", status: "Requested" }),
    u({ id: "6", email: "f@x.test", status: "Requested" }),
  ];

  it("counts each bucket, leaving pending requests out of all of them", () => {
    expect(countByStatus(users)).toEqual({ all: 4, active: 2, invited: 1, disabled: 1 });
  });

  /// The counts are rendered on the chips, so a total that disagrees with the three parts would be a lie
  /// sitting permanently on screen.
  it("keeps all equal to the three buckets it is made of", () => {
    const c = countByStatus(users);
    expect(c.all).toBe(c.active + c.invited + c.disabled);
  });

  it("counts nothing without falling over", () => {
    expect(countByStatus([])).toEqual({ all: 0, active: 0, invited: 0, disabled: 0 });
  });
});

describe("filterUsers", () => {
  const users = [
    u({ id: "1", email: "priya.shah@x.test", fullName: "Priya Shah" }),
    u({ id: "2", email: "tom@x.test", fullName: "Tom Okafor", status: "Invited" }),
    u({ id: "3", email: "anna@x.test", fullName: "Anna Weiss", isEnabled: false }),
    u({ id: "4", email: "pending@x.test", status: "Requested" }),
  ];
  const ids = (list: AdminUser[]) => list.map((x) => x.id);

  it("drops pending requests entirely, even with no search and no filter", () => {
    expect(ids(filterUsers(users, "", "all"))).toEqual(["1", "2", "3"]);
  });

  it("matches on name", () => {
    expect(ids(filterUsers(users, "okafor", "all"))).toEqual(["2"]);
  });

  it("matches on email when the name does not", () => {
    expect(ids(filterUsers(users, "priya.shah@", "all"))).toEqual(["1"]);
  });

  it("ignores case and surrounding space", () => {
    expect(ids(filterUsers(users, "  PRIYA  ", "all"))).toEqual(["1"]);
  });

  it("matches a user who has no name at all", () => {
    const anon = [u({ id: "9", email: "solo@x.test" })];
    expect(ids(filterUsers(anon, "solo", "all"))).toEqual(["9"]);
  });

  it("narrows to one bucket", () => {
    expect(ids(filterUsers(users, "", "invited"))).toEqual(["2"]);
    expect(ids(filterUsers(users, "", "disabled"))).toEqual(["3"]);
  });

  it("applies the search and the bucket together", () => {
    expect(ids(filterUsers(users, "a", "disabled"))).toEqual(["3"]);
    expect(ids(filterUsers(users, "tom", "disabled"))).toEqual([]);
  });
});

describe("effectivePermissionBits", () => {
  const groups = [
    g({ id: "g1", name: "Admins", permissions: 1 | 2, memberIds: ["u1"] }),
    g({ id: "g2", name: "Research", permissions: 2 | 16, memberIds: ["u1", "u2"] }),
    g({ id: "g3", name: "Nobody", permissions: 4, memberIds: [] }),
  ];

  /// Both halves have always been on the wire - group permissions and group membership - but nothing has
  /// ever joined them, so an administrator could not see what a person could actually do.
  it("unions the bits of every group the user is in", () => {
    expect(effectivePermissionBits("u1", groups)).toBe(1 | 2 | 16);
  });

  it("counts an overlapping bit once", () => {
    expect(effectivePermissionBits("u2", groups)).toBe(2 | 16);
  });

  it("ignores groups the user is not in", () => {
    expect(effectivePermissionBits("u1", groups) & 4).toBe(0);
  });

  it("grants nothing to someone in no groups", () => {
    expect(effectivePermissionBits("nobody", groups)).toBe(0);
  });
});
