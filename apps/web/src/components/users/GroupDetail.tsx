import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import type { AdminUser, Group } from "../../lib/types";
import AddMemberTypeahead from "../AddMemberTypeahead";
import EditGroupDialog from "./EditGroupDialog";
import UserAvatar from "./UserAvatar";
import { PERMISSION_BITS } from "./permissions";

const heading = "text-[10px] font-medium uppercase tracking-[.06em] text-gray-400 dark:text-gray-600";

/// Chips beyond this are hidden behind a "show all". A group with a hundred members would otherwise bury the
/// permissions above it under a wall of names.
const CHIP_CAP = 30;

/// One group: what it is, what it lets people do, and who is in it.
///
/// The permissions used to be a row of bare checkboxes under column headings. A column headed
/// `MANAGE PLATFORM` tells an administrator nothing about the blast radius; the sentence beside each one
/// does. Membership used to be a separate popup and is now edited here.
export default function GroupDetail({ group, users }: { group: Group; users: AdminUser[] }) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [showAllMembers, setShowAllMembers] = useState(false);

  const onError = (e: unknown) => setError(apiErrorMessage(e));
  const invalidate = () => qc.invalidateQueries({ queryKey: ["groups"] });

  const update = useMutation({
    // The whole shape, not a patch - see EditGroupDialog.
    mutationFn: (permissions: number) =>
      api.updateGroup(group.id, {
        name: group.name,
        description: group.description,
        icon: group.icon,
        color: group.color,
        permissions,
      }),
    onSuccess: invalidate,
    onError,
  });
  const remove = useMutation({ mutationFn: () => api.deleteGroup(group.id), onSuccess: invalidate, onError });
  const addMember = useMutation({
    mutationFn: (userId: string) => api.addGroupMember(group.id, userId),
    onSuccess: invalidate,
    onError,
  });
  const removeMember = useMutation({
    mutationFn: (userId: string) => api.removeGroupMember(group.id, userId),
    onSuccess: invalidate,
    onError,
  });

  const members = users.filter((u) => group.memberIds.includes(u.id));
  const visible = showAllMembers ? members : members.slice(0, CHIP_CAP);
  // The server refuses to empty the system group, so the last ✕ is disabled rather than left to fail.
  const lastSystemMember = group.isSystem && members.length === 1;

  return (
    <div className="min-h-0 min-w-0 flex-1 space-y-4 overflow-y-auto p-5">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate text-[15px] font-semibold dark:text-gray-100">{group.name}</h3>
          {group.description && (
            <p className="mt-0.5 text-[11px] text-gray-500 dark:text-gray-400">{group.description}</p>
          )}
        </div>
        {/* The server refuses to rename or delete the system group, so neither control is offered for it. */}
        {!group.isSystem && (
          <div className="flex shrink-0 gap-2">
            <button
              type="button"
              data-testid={`edit-group-${group.id}`}
              onClick={() => setEditing(true)}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("editGroup")}
            </button>
            <button
              type="button"
              data-testid={`delete-group-${group.id}`}
              onClick={() => {
                setError(null);
                if (window.confirm(t("confirmDeleteGroup", { name: group.name }))) remove.mutate();
              }}
              className="rounded border border-red-300 px-3 py-1 text-sm text-red-600 dark:border-red-800 dark:text-red-400"
            >
              {t("deleteGroup")}
            </button>
          </div>
        )}
      </div>

      {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}

      <section className="space-y-0.5">
        <h4 className={heading}>{t("whatThisGroupCanDo")}</h4>
        {PERMISSION_BITS.map((p) => (
          <label
            key={p.bit}
            className="flex gap-2.5 border-b border-gray-100 py-2.5 last:border-b-0 dark:border-gray-800"
          >
            <input
              type="checkbox"
              data-testid={`perm-${group.id}-${p.bit}`}
              aria-label={`${group.name}: ${t(p.key)}`}
              checked={(group.permissions & p.bit) !== 0}
              // Disabled, not hidden: the system group's rights are fixed, and hiding them would hide
              // what it can do.
              disabled={group.isSystem || update.isPending}
              onChange={() => update.mutate(group.permissions ^ p.bit)}
              className="mt-0.5 shrink-0"
            />
            <span className="min-w-0">
              <span className="block text-sm dark:text-gray-100">{t(p.key)}</span>
              <span className="block text-[11px] text-gray-500 dark:text-gray-400">{t(p.hint)}</span>
            </span>
          </label>
        ))}
        {group.isSystem && (
          <p className="pt-2 text-[11px] text-gray-500 dark:text-gray-400">{t("groupSystemHint")}</p>
        )}
      </section>

      <section className="space-y-2">
        <div className="flex items-center justify-between gap-2">
          <h4 className={heading}>
            {t("groupMembers")} · {members.length}
          </h4>
        </div>

        <AddMemberTypeahead
          users={users}
          excludeIds={group.memberIds}
          onAdd={(userId) => addMember.mutate(userId)}
          label={t("addMembers")}
        />

        {members.length === 0 ? (
          <p className="text-xs text-gray-400 dark:text-gray-500">{t("noMembers")}</p>
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {visible.map((u) => (
              <span
                key={u.id}
                className="inline-flex items-center gap-1.5 rounded-full border border-gray-200 bg-white py-0.5 pl-0.5 pr-2 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-200"
              >
                <UserAvatar user={u} />
                <span className="max-w-[12rem] truncate">{u.fullName || u.email}</span>
                <button
                  type="button"
                  data-testid={`member-${group.id}-${u.id}`}
                  disabled={lastSystemMember}
                  onClick={() => removeMember.mutate(u.id)}
                  aria-label={t("removeFromGroupAria", { name: u.fullName || u.email, group: group.name })}
                  className="text-gray-400 hover:text-red-600 disabled:opacity-40 dark:hover:text-red-400"
                >
                  ✕
                </button>
              </span>
            ))}
            {!showAllMembers && members.length > CHIP_CAP && (
              <button
                type="button"
                onClick={() => setShowAllMembers(true)}
                className="rounded-full border border-dashed border-gray-300 px-2.5 py-[3px] text-xs text-gray-600 dark:border-gray-600 dark:text-gray-300"
              >
                {t("showAllMembers", { n: members.length })}
              </button>
            )}
          </div>
        )}
      </section>

      {editing && <EditGroupDialog group={group} onClose={() => setEditing(false)} />}
    </div>
  );
}
