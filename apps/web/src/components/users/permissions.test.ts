import { describe, expect, it } from "vitest";
import { PERMISSION_BITS, grantKeys, permissionCount } from "./permissions";

/// This console is the only way to grant a platform permission, so a bit missing from this list is a
/// permission nobody can ever hold - which is how ManagePeople shipped with the People page unreachable.
///
/// The list mirrors the server's PlatformPermission enum by hand. This asserts the mirror is complete and
/// contiguous; `SeederPeoplePermissionTests` asserts the server side.
describe("permission bits", () => {
  /// Update this when a permission is added, and add the bit above. Keeping the expected set here rather
  /// than deriving it is the point: the test should fail loudly when the enum grows.
  const EXPECTED = [1, 2, 4, 8, 16];

  it("covers every platform permission bit", () => {
    expect(PERMISSION_BITS.map((p) => p.bit)).toEqual(EXPECTED);
  });

  it("is append-only powers of two, so a stored bitmask never shifts meaning", () => {
    for (const [i, { bit }] of PERMISSION_BITS.entries()) expect(bit).toBe(2 ** i);
  });

  it("gives every bit a distinct label key", () => {
    const keys = PERMISSION_BITS.map((p) => p.key);
    expect(new Set(keys).size).toBe(keys.length);
  });

  /// Three registers, three key sets. A bit that reused one for another would put a Title Case name in the
  /// middle of the Grants sentence, or a whole sentence on a checkbox.
  it("gives every bit a distinct hint and grant key too", () => {
    for (const field of ["hint", "grant"] as const) {
      const keys = PERMISSION_BITS.map((p) => p[field]);
      expect(new Set(keys).size, `duplicate ${field} key`).toBe(keys.length);
    }
  });
});

describe("permissionCount", () => {
  it("counts the bits that are set", () => {
    expect(permissionCount(0)).toBe(0);
    expect(permissionCount(1)).toBe(1);
    expect(permissionCount(1 | 2 | 16)).toBe(3);
    expect(permissionCount(31)).toBe(5);
  });

  /// A bitmask stored by a newer release, or left behind by a removed permission, must not be reported as a
  /// permission this build knows nothing about.
  it("ignores a bit that is not a known permission", () => {
    expect(permissionCount(1 | 1024)).toBe(1);
  });
});

describe("grantKeys", () => {
  it("returns the lowercase fragments in bit order", () => {
    expect(grantKeys(16 | 1)).toEqual(["grantManageRooms", "grantManagePeople"]);
  });

  it("returns nothing for no permissions", () => {
    expect(grantKeys(0)).toEqual([]);
  });
});
