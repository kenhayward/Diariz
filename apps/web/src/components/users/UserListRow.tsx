import { useTranslation } from "react-i18next";
import type { AdminUser } from "../../lib/types";
import StatusPill from "./StatusPill";
import StorageBar, { storageText } from "./StorageBar";
import UserAvatar from "./UserAvatar";

/// One row in the Users list.
///
/// A button, not a table row: the row's whole job is to select, and its accessible name carries the three
/// facts a screen reader needs in one go (name, email, status) rather than making the user walk five cells.
/// The visible columns still line up because every row uses the same fixed widths.
export default function UserListRow({
  user,
  groupNames,
  selected,
  onSelect,
}: {
  user: AdminUser;
  groupNames: string[];
  selected: boolean;
  onSelect: () => void;
}) {
  const { t } = useTranslation("admin");
  const name = user.fullName || user.email;
  const status = !user.isEnabled
    ? t("statusDisabled")
    : user.status === "Invited"
      ? t("statusAwaitingSetup")
      : t("statusActive");

  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      aria-label={`${name}, ${user.email}, ${status}`}
      className={`flex w-full items-center gap-2.5 border-b border-gray-100 px-3.5 py-2.5 text-left dark:border-gray-800 ${
        selected ? "bg-blue-50 dark:bg-blue-500/[0.14]" : "hover:bg-gray-50 dark:hover:bg-gray-800/50"
      }`}
    >
      <UserAvatar user={user} />

      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5">
          <span className="truncate text-[13px] dark:text-gray-100">{name}</span>
          <StatusPill user={user} />
        </span>
        {/* Only when the name is not already the email, or the row says the same thing twice. */}
        {user.fullName && (
          <span className="block truncate text-[11px] text-gray-500 dark:text-gray-400">{user.email}</span>
        )}
      </span>

      <span className="w-[170px] shrink-0 truncate text-[11px] text-gray-500 dark:text-gray-400">
        {groupNames.length > 0 ? groupNames.join(", ") : "-"}
      </span>

      <span className="w-[120px] shrink-0">
        <StorageBar usedBytes={user.usedBytes} quotaBytes={user.quotaBytes} />
        <span className="mt-0.5 block text-[10px] tabular-nums text-gray-500 dark:text-gray-400">
          {storageText(user.usedBytes, user.quotaBytes)}
        </span>
      </span>
    </button>
  );
}
