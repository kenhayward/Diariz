import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import { bytesToGb, gbToBytes } from "../../lib/format";
import type { AdminUser, Group } from "../../lib/types";
import StatusPill from "./StatusPill";
import StorageBar, { storageText } from "./StorageBar";
import UserAvatar from "./UserAvatar";
import { effectivePermissionBits } from "./userFilters";
import { grantKeys } from "./permissions";

const heading = "text-[10px] font-medium uppercase tracking-[.06em] text-gray-400 dark:text-gray-600";

/// Everything about the selected user, in one pane.
///
/// The pane exists mostly for one line. `Grants:` joins a person's group memberships to those groups'
/// permissions - both of which the server has always sent - and says in plain language what they can do.
/// Before this, answering that meant reading a checkbox matrix and cross-referencing membership by eye.
export default function UserDetail({
  user,
  groups,
  isSelf,
  maxQuotaBytes,
  onDeleted,
}: {
  user: AdminUser;
  groups: Group[];
  isSelf: boolean;
  maxQuotaBytes: number | null;
  onDeleted: () => void;
}) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const [quotaGb, setQuotaGb] = useState(String(bytesToGb(user.quotaBytes)));

  // Reseed when the selection moves, or the field would keep the previous user's number.
  useEffect(() => setQuotaGb(String(bytesToGb(user.quotaBytes))), [user.id, user.quotaBytes]);

  const onError = (e: unknown) => setError(apiErrorMessage(e));
  const refreshUsers = () => qc.invalidateQueries({ queryKey: ["admin-users"] });
  // Membership changes move the Groups column and the Grants line, both of which read `["groups"]`.
  const refreshGroups = () => qc.invalidateQueries({ queryKey: ["groups"] });

  const setEnabled = useMutation({
    mutationFn: (isEnabled: boolean) => api.setUserEnabled(user.id, isEnabled),
    onSuccess: refreshUsers,
    onError,
  });
  const setQuota = useMutation({
    mutationFn: (bytes: number) => api.setUserQuota(user.id, bytes),
    onSuccess: async () => {
      await refreshUsers();
      // Editing your own quota changes the figure in the account menu.
      await qc.invalidateQueries({ queryKey: ["user-storage"] });
    },
    onError,
  });
  const removeUser = useMutation({
    mutationFn: () => api.deleteUser(user.id),
    onSuccess: async () => {
      await refreshUsers();
      onDeleted();
    },
    onError,
  });
  const joinGroup = useMutation({
    mutationFn: (groupId: string) => api.addGroupMember(groupId, user.id),
    onSuccess: refreshGroups,
    onError,
  });
  const leaveGroup = useMutation({
    mutationFn: (groupId: string) => api.removeGroupMember(groupId, user.id),
    onSuccess: refreshGroups,
    onError,
  });

  const memberOf = groups.filter((g) => g.memberIds.includes(user.id));
  const grants = grantKeys(effectivePermissionBits(user.id, groups)).map((k) => t(k));
  const isPlatform = user.accountType === "PlatformAdministrator";
  // The server refuses both, so say why rather than rendering an empty space where the buttons were.
  const protectedReason = isPlatform ? t("protectedPlatformAdmin") : isSelf ? t("protectedSelf") : null;
  const maxGb = maxQuotaBytes != null ? bytesToGb(maxQuotaBytes) : undefined;

  return (
    <div className="w-[340px] shrink-0 space-y-4 overflow-y-auto border-l border-gray-200 bg-gray-50 p-4 dark:border-gray-700 dark:bg-gray-950/40">
      <div className="flex items-center gap-3">
        <UserAvatar user={user} size="md" />
        <div className="min-w-0">
          <div className="truncate text-sm font-semibold dark:text-gray-100">{user.fullName || user.email}</div>
          <div className="truncate text-[11px] text-gray-500 dark:text-gray-400">{user.email}</div>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-1.5">
        <StatusPill user={user} />
        {user.hasGoogle && (
          <span className="rounded bg-blue-100 px-1.5 py-0.5 text-[10px] text-blue-800 dark:bg-blue-900/40 dark:text-blue-300">
            {t("google")}
          </span>
        )}
      </div>

      {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}

      {protectedReason ? (
        <p className="text-[11px] text-gray-500 dark:text-gray-400">{protectedReason}</p>
      ) : (
        <button
          type="button"
          onClick={() => setEnabled.mutate(!user.isEnabled)}
          disabled={setEnabled.isPending}
          className="w-full rounded border border-gray-300 bg-white px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-900 dark:text-gray-200 dark:hover:bg-gray-800"
        >
          {user.isEnabled ? t("disable") : t("enable")}
        </button>
      )}

      <section className="space-y-1.5">
        <h3 className={heading}>{t("sectionGroups")}</h3>
        <div className="flex flex-wrap items-center gap-1.5">
          {memberOf.map((g) => (
            <span
              key={g.id}
              className="inline-flex items-center gap-1 rounded-full border border-gray-200 bg-white px-2.5 py-[3px] text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-200"
            >
              {g.name}
              <button
                type="button"
                onClick={() => leaveGroup.mutate(g.id)}
                aria-label={t("removeFromGroupAria", { name: user.fullName || user.email, group: g.name })}
                className="text-gray-400 hover:text-red-600 dark:hover:text-red-400"
              >
                ✕
              </button>
            </span>
          ))}
          <AddToGroup
            groups={groups.filter((g) => !g.memberIds.includes(user.id))}
            onPick={(id) => joinGroup.mutate(id)}
          />
        </div>
        <p className="text-[11px] text-gray-500 dark:text-gray-400">
          {grants.length > 0 ? t("grantsLine", { list: grants.join(", ") }) : t("grantsNone")}
        </p>
      </section>

      <section className="space-y-1.5">
        <h3 className={heading}>{t("sectionStorage")}</h3>
        <div className="flex items-center gap-2">
          <div className="min-w-0 flex-1">
            <StorageBar usedBytes={user.usedBytes} quotaBytes={user.quotaBytes} height={5} />
          </div>
          <span className="shrink-0 text-[11px] tabular-nums text-gray-600 dark:text-gray-300">
            {storageText(user.usedBytes, user.quotaBytes)}
          </span>
        </div>
        <form
          className="flex flex-wrap items-center gap-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            setError(null);
            setQuota.mutate(gbToBytes(Number(quotaGb)));
          }}
        >
          <label className="text-[11px] text-gray-600 dark:text-gray-300" htmlFor={`quota-${user.id}`}>
            {t("quota")}
          </label>
          <input
            id={`quota-${user.id}`}
            type="number"
            min={0}
            step={0.5}
            max={maxGb}
            value={quotaGb}
            onChange={(e) => setQuotaGb(e.target.value)}
            aria-label={t("quotaForAria", { email: user.email })}
            className="w-20 rounded border border-gray-300 px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <span className="text-[11px] text-gray-600 dark:text-gray-300">{t("gb")}</span>
          <button
            type="submit"
            disabled={setQuota.isPending}
            className="rounded border border-gray-300 bg-white px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:bg-gray-900 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:save")}
          </button>
          {maxGb != null && (
            <span className="text-[11px] text-gray-400 dark:text-gray-500">{t("maxGb", { max: maxGb })}</span>
          )}
        </form>
      </section>

      {!protectedReason && (
        <div className="border-t border-gray-200 pt-3 dark:border-gray-700">
          <button
            type="button"
            onClick={() => {
              setError(null);
              if (window.confirm(t("confirmDeleteUser", { email: user.email }))) removeUser.mutate();
            }}
            disabled={removeUser.isPending}
            className="text-sm text-red-600 hover:underline disabled:opacity-50 dark:text-red-400"
          >
            {t("deleteAccount")}
          </button>
        </div>
      )}
    </div>
  );
}

/// A `+ Add to group` chip that turns into a picker. Kept inline rather than always showing a select, so the
/// chip row reads as the user's groups plus one affordance, not as a form.
function AddToGroup({ groups, onPick }: { groups: Group[]; onPick: (id: string) => void }) {
  const { t } = useTranslation("admin");
  const [open, setOpen] = useState(false);
  if (groups.length === 0) return null;

  if (!open) {
    return (
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="rounded-full border border-dashed border-gray-300 px-2.5 py-[3px] text-xs text-gray-600 hover:border-blue-400 hover:text-blue-700 dark:border-gray-600 dark:text-gray-300 dark:hover:border-blue-500 dark:hover:text-blue-300"
      >
        {t("addToGroup")}
      </button>
    );
  }
  return (
    <select
      autoFocus
      aria-label={t("addToGroup")}
      defaultValue=""
      onChange={(e) => {
        if (e.target.value) onPick(e.target.value);
        setOpen(false);
      }}
      onBlur={() => setOpen(false)}
      className="rounded border border-gray-300 px-2 py-1 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
    >
      <option value="" disabled>
        {t("addToGroup")}
      </option>
      {groups.map((g) => (
        <option key={g.id} value={g.id}>
          {g.name}
        </option>
      ))}
    </select>
  );
}
