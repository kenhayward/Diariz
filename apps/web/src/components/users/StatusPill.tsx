import { useTranslation } from "react-i18next";
import type { AdminUser } from "../../lib/types";

/// A user's state as one pill.
///
/// `isEnabled` is checked before `status` for the same reason `bucketOf` does it: an account can be Active and
/// switched off at once, and the fact worth showing is that it cannot sign in. The old table said this twice -
/// a status pill *and* a separate "disabled" badge beside the name - which left an admin reading two labels
/// that appeared to contradict each other.
///
/// The word is always present, never colour alone.
export default function StatusPill({ user }: { user: Pick<AdminUser, "status" | "isEnabled"> }) {
  const { t } = useTranslation("admin");
  const [label, tone] = !user.isEnabled
    ? [t("statusDisabled"), "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300"]
    : user.status === "Requested"
      ? [t("statusRequested"), "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300"]
      : user.status === "Invited"
        ? [t("statusAwaitingSetup"), "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300"]
        : [t("statusActive"), "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300"];

  return <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${tone}`}>{label}</span>;
}
