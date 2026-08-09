import Avatar from "../Avatar";
import { initialsFromEmail, initialsFromName } from "../../lib/initials";
import type { AdminUser } from "../../lib/types";

/// An `AdminUser`'s avatar. Thin, but it exists so the initials fallback is written once: the console shows a
/// face in list rows, the detail pane, request rows and member chips, and all four must agree with the account
/// menu (`auth.tsx`) about what to show when there is no picture.
///
/// `pictureUrl` is the linked Google account's photo. Password-only accounts have none and fall back to
/// initials - name first, email second, exactly as the account menu does.
export default function UserAvatar({
  user,
  size = "xs",
}: {
  user: Pick<AdminUser, "email" | "fullName" | "pictureUrl">;
  size?: "xs" | "sm" | "md" | "lg";
}) {
  const initials = user.fullName ? initialsFromName(user.fullName) : initialsFromEmail(user.email);
  return <Avatar initials={initials} pictureUrl={user.pictureUrl} size={size} />;
}
