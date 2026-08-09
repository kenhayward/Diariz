/// Pure list logic for the Users & access console: which bucket an account falls in, what the filter chips
/// count, what the search box matches, and what a person can actually do.
///
/// It is separated from the components so the rules can be pinned without rendering a modal - the same split
/// as `lib/dayGrid.ts`. Everything here works off the two queries the console already loads
/// (`["admin-users"]` and `["groups"]`); nothing needs a server round trip.

import type { AdminUser, Group } from "../../lib/types";

/// The Users tab's filter chips. `all` is the other three added together - **not** every row the server sent,
/// because a pending request is not an account yet and belongs on the Requests tab.
export type StatusFilter = "all" | "active" | "invited" | "disabled";

/// The bucket a user is counted and filtered under, or null when they are a pending request.
///
/// The order is the point: an account can be Active *and* switched off, and it must be counted exactly once.
/// `isEnabled` is checked first because "disabled" is the fact an administrator is scanning for - an account
/// that cannot sign in is not usefully described as active.
export function bucketOf(u: AdminUser): Exclude<StatusFilter, "all"> | null {
  if (u.status === "Requested") return null;
  if (!u.isEnabled) return "disabled";
  return u.status === "Invited" ? "invited" : "active";
}

/// The four numbers printed on the chips. `all` is derived from the parts rather than from the array length,
/// so the total can never drift from the buckets beside it.
export function countByStatus(users: AdminUser[]): Record<StatusFilter, number> {
  const counts = { all: 0, active: 0, invited: 0, disabled: 0 };
  for (const u of users) {
    const bucket = bucketOf(u);
    if (bucket === null) continue;
    counts[bucket] += 1;
    counts.all += 1;
  }
  return counts;
}

/// The rows the Users tab shows: the chosen bucket, narrowed by a case-insensitive substring of the name or
/// the email. Both are searched because an administrator arriving from a support request has one or the
/// other, rarely both.
export function filterUsers(users: AdminUser[], search: string, status: StatusFilter): AdminUser[] {
  const q = search.trim().toLowerCase();
  return users.filter((u) => {
    const bucket = bucketOf(u);
    if (bucket === null) return false;
    if (status !== "all" && bucket !== status) return false;
    if (!q) return true;
    return (u.fullName ?? "").toLowerCase().includes(q) || u.email.toLowerCase().includes(q);
  });
}

/// The union of the platform permissions granted by every group a user belongs to.
///
/// This is the console's one genuinely new fact. The server has always sent both halves - a group's
/// permission bits and its member ids - but nothing has ever joined them, so answering "what can this person
/// do?" meant reading the permission matrix and cross-referencing membership by eye.
export function effectivePermissionBits(userId: string, groups: Group[]): number {
  let bits = 0;
  for (const g of groups) if (g.memberIds.includes(userId)) bits |= g.permissions;
  return bits;
}
