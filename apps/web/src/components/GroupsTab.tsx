import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Group } from "../lib/types";
import GroupMembersModal from "./GroupMembersModal";

/// The PlatformPermission bits, mirrored from the server enum. Append-only: never renumber.
const PERMISSION_BITS = [
  { bit: 1, key: "permManageRooms" },
  { bit: 2, key: "permManageUsers" },
  { bit: 4, key: "permManagePlatform" },
  { bit: 8, key: "permManageFormulas" },
] as const;

/// Groups administration: create a group, choose what it may do, and pick its members.
///
/// The system group (Platform Administrators) is protected - the server refuses to delete it, rename it, or
/// change its permissions, and refuses to remove its last member - so those controls are absent or disabled
/// here rather than failing on submit.
export default function GroupsTab() {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { data: groups = [] } = useQuery({ queryKey: ["groups"], queryFn: api.listGroups });
  const [newName, setNewName] = useState("");
  // The group whose membership dialog is open (null = closed). Membership is managed in a focused popup so it
  // scales past ~100 users, rather than the old inline list of every user as checkboxes.
  const [membersGroup, setMembersGroup] = useState<Group | null>(null);
  const [error, setError] = useState<string | null>(null);

  const invalidate = () => qc.invalidateQueries({ queryKey: ["groups"] });
  const onError = (e: unknown) => setError(e instanceof Error ? e.message : String(e));

  const create = useMutation({
    mutationFn: (name: string) => api.createGroup({ name, permissions: 0 }),
    onSuccess: () => {
      setNewName("");
      void invalidate();
    },
    onError,
  });
  const remove = useMutation({ mutationFn: (id: string) => api.deleteGroup(id), onSuccess: invalidate, onError });
  const update = useMutation({
    mutationFn: ({ id, group }: { id: string; group: Group }) =>
      api.updateGroup(id, {
        name: group.name,
        description: group.description,
        icon: group.icon,
        color: group.color,
        permissions: group.permissions,
      }),
    onSuccess: invalidate,
    onError,
  });
  function togglePermission(group: Group, bit: number) {
    update.mutate({ id: group.id, group: { ...group, permissions: group.permissions ^ bit } });
  }

  function onDelete(group: Group) {
    setError(null);
    if (window.confirm(t("confirmDeleteGroup", { name: group.name }))) remove.mutate(group.id);
  }

  return (
    <div className="space-y-3">
      {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}

      <table className="w-full text-sm">
        <thead>
          <tr className="text-left text-xs uppercase text-gray-500 dark:text-gray-400">
            <th className="py-1">{t("groupName")}</th>
            {PERMISSION_BITS.map((p) => (
              <th key={p.bit} className="px-2 font-normal">{t(p.key)}</th>
            ))}
            <th className="px-2 font-normal">{t("groupMembers")}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
              <tr key={g.id} className="border-t align-middle dark:border-gray-700 dark:text-gray-200">
                <td className="py-2 pr-2">
                  <span className="font-medium">{g.name}</span>
                  {g.isSystem && (
                    <span className="ml-2 rounded bg-gray-100 px-1.5 py-0.5 text-[10px] text-gray-600 dark:bg-gray-800 dark:text-gray-300">
                      {t("groupSystem")}
                    </span>
                  )}
                </td>
                {PERMISSION_BITS.map((p) => (
                  <td key={p.bit} className="px-2 text-center">
                    <input
                      type="checkbox"
                      data-testid={`perm-${g.id}-${p.bit}`}
                      aria-label={`${g.name}: ${t(p.key)}`}
                      checked={(g.permissions & p.bit) !== 0}
                      disabled={g.isSystem}
                      onChange={() => togglePermission(g, p.bit)}
                    />
                  </td>
                ))}
                <td className="px-2 text-center">
                  <button
                    type="button"
                    data-testid={`members-${g.id}`}
                    onClick={() => setMembersGroup(g)}
                    title={t("manageMembers")}
                    className="rounded border px-2 py-0.5 text-xs dark:border-gray-700 dark:hover:bg-gray-800"
                  >
                    {g.memberIds.length}
                  </button>
                </td>
                <td className="py-2 text-right">
                  {!g.isSystem && (
                    <button
                      type="button"
                      data-testid={`delete-group-${g.id}`}
                      onClick={() => onDelete(g)}
                      className="rounded px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30"
                    >
                      {t("deleteGroup")}
                    </button>
                  )}
                </td>
              </tr>
          ))}
        </tbody>
      </table>

      <form
        data-testid="new-group-form"
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setError(null);
          if (newName.trim()) create.mutate(newName.trim());
        }}
      >
        <input
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          placeholder={t("newGroupName")}
          aria-label={t("newGroupName")}
          className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <button
          type="submit"
          disabled={!newName.trim()}
          className="shrink-0 rounded bg-blue-600 px-3 py-1 text-sm text-white disabled:opacity-50"
        >
          {t("addGroup")}
        </button>
      </form>

      {membersGroup && (
        <GroupMembersModal group={membersGroup} onClose={() => setMembersGroup(null)} />
      )}
    </div>
  );
}
