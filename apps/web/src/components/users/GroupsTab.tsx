import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import MeetingTypeIcon from "../MeetingTypeIcon";
import GroupDetail from "./GroupDetail";
import { DEFAULT_GROUP_COLOR } from "./EditGroupDialog";
import { permissionCount } from "./permissions";

/// Groups administration: pick a group on the left, see and change everything about it on the right.
///
/// It replaces a permission matrix - one row per group, one column per permission bit, a grid of bare
/// checkboxes. That fitted on screen only while both axes stayed small, and it never had room to say what a
/// permission actually did.
export default function GroupsTab() {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { data: groups = [] } = useQuery({ queryKey: ["groups"], queryFn: api.listGroups });
  // Member chips need names, and the picker needs everyone to search over.
  const { data: users = [] } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [newName, setNewName] = useState("");
  const [error, setError] = useState<string | null>(null);

  const create = useMutation({
    mutationFn: (name: string) => api.createGroup({ name, permissions: 0 }),
    onSuccess: async (g) => {
      setNewName("");
      await qc.invalidateQueries({ queryKey: ["groups"] });
      // Land on what you just made - otherwise creating a group appears to do nothing.
      setSelectedId(g.id);
    },
    onError: (e: unknown) => setError(apiErrorMessage(e)),
  });

  // The id, not the group: every edit refetches the list, and holding the object would pin a stale copy.
  const selected = groups.find((g) => g.id === selectedId) ?? null;

  return (
    <div className="flex min-h-0 min-w-0 flex-1">
      <div className="flex w-[280px] shrink-0 flex-col border-r border-gray-200 dark:border-gray-700">
        <div className="min-h-0 flex-1 overflow-y-auto">
          {groups.map((g) => (
            <button
              key={g.id}
              type="button"
              onClick={() => setSelectedId(g.id)}
              aria-pressed={g.id === selectedId}
              className={`flex w-full items-start gap-2.5 border-b border-gray-100 px-3.5 py-2.5 text-left dark:border-gray-800 ${
                g.id === selectedId
                  ? "bg-blue-50 dark:bg-blue-500/[0.14]"
                  : "hover:bg-gray-50 dark:hover:bg-gray-800/50"
              }`}
            >
              <span className="mt-0.5 shrink-0" aria-hidden>
                {g.icon ? (
                  <MeetingTypeIcon icon={g.icon} color={g.color ?? DEFAULT_GROUP_COLOR} size={14} />
                ) : (
                  <span
                    className="block h-3.5 w-3.5 rounded-[3px]"
                    style={{ backgroundColor: g.color ?? DEFAULT_GROUP_COLOR }}
                  />
                )}
              </span>
              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-1.5">
                  <span className="truncate text-sm dark:text-gray-100">{g.name}</span>
                  {g.isSystem && (
                    <span className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 text-[10px] text-gray-600 dark:bg-gray-800 dark:text-gray-300">
                      {t("groupSystem")}
                    </span>
                  )}
                </span>
                <span className="block truncate text-[11px] text-gray-500 dark:text-gray-400">
                  {t("groupPermCount", { count: permissionCount(g.permissions) })} ·{" "}
                  {t("groupMemberCount", { count: g.memberIds.length })}
                </span>
              </span>
            </button>
          ))}
        </div>

        <form
          data-testid="new-group-form"
          className="shrink-0 space-y-2 border-t border-gray-200 p-2 dark:border-gray-700"
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
            className="w-full rounded border border-gray-300 px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <button
            type="submit"
            disabled={!newName.trim() || create.isPending}
            className="w-full rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("newGroup")}
          </button>
        </form>
      </div>

      {selected ? (
        <GroupDetail group={selected} users={users} />
      ) : (
        <div className="min-w-0 flex-1 p-5 text-sm text-gray-500 dark:text-gray-400">
          {error && <p className="mb-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
          {t("pickGroup")}
        </div>
      )}
    </div>
  );
}
