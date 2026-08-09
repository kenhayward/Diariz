/// The PlatformPermission bits, mirrored from the server enum. Append-only: never renumber.
///
/// This console is the only way to grant a platform permission, so a bit missing here is a permission nobody
/// can ever hold. ManagePeople shipped that way once and left the People page unreachable;
/// `groupsPermissionBits.test.ts` now fails when the list falls behind the enum.
///
/// It lives in its own module rather than beside the Groups tab because two screens read it now: the group
/// detail lists the bits with a sentence each, and the user detail turns a person's union of them into a
/// plain-language line.
///
/// Each bit carries three catalogue keys, and they are deliberately different registers:
/// - `key` is the Title Case name  ("Manage rooms")
/// - `hint` is the sentence under it ("Create shared rooms, and add or remove their members.")
/// - `grant` is a lowercase fragment for joining into a sentence ("manage rooms")
export const PERMISSION_BITS = [
  { bit: 1, key: "permManageRooms", hint: "permHintManageRooms", grant: "grantManageRooms" },
  { bit: 2, key: "permManageUsers", hint: "permHintManageUsers", grant: "grantManageUsers" },
  { bit: 4, key: "permManagePlatform", hint: "permHintManagePlatform", grant: "grantManagePlatform" },
  { bit: 8, key: "permManageFormulas", hint: "permHintManageFormulas", grant: "grantManageFormulas" },
  { bit: 16, key: "permManagePeople", hint: "permManagePeopleHint", grant: "grantManagePeople" },
] as const;

/// How many permissions a bitmask actually grants. Counted against the known bits rather than by popcount, so
/// a stale bit left in the database by an older release is not reported as a permission.
export function permissionCount(bits: number): number {
  return PERMISSION_BITS.filter((p) => (bits & p.bit) !== 0).length;
}

/// The catalogue keys for the lowercase fragments a bitmask grants, in bit order, for joining into the
/// user detail's "Grants: ..." line.
export function grantKeys(bits: number): string[] {
  return PERMISSION_BITS.filter((p) => (bits & p.bit) !== 0).map((p) => p.grant);
}
