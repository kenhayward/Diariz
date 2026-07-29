import { describe, expect, it } from "vitest";
import { PERMISSION_BITS } from "./GroupsTab";

/// The Groups screen is the only way to grant a platform permission, so a bit missing from this list is a
/// permission nobody can ever hold - which is how ManagePeople shipped with the People page unreachable.
///
/// The list mirrors the server's PlatformPermission enum by hand. This asserts the mirror is complete and
/// contiguous; `SeederPeoplePermissionTests` asserts the server side.
describe("GroupsTab permission bits", () => {
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
});
