import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Group } from "../lib/types";
import AddMemberTypeahead from "./AddMemberTypeahead";

/// A focused, nested dialog for managing ONE group's membership. Opened from the Groups tab's member-count
/// button. Shows the group's CURRENT members (each removable) plus a "search to add" type-ahead - discoverable
/// and scalable past ~100 users (unlike the old flat checkbox list of every user). Dismisses on the Close
/// button or Escape only, never on a backdrop click (consistent with the parent modal). The root carries
/// `data-nested-dialog` so the parent modal's Escape handler defers to this one while it is open.
export default function GroupMembersModal({ group, onClose }: { group: Group; onClose: () => void }) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const [error, setError] = useState<string | null>(null);

  // Read the live group + user list from cache so membership updates after an add/remove (the `group` prop is
  // a snapshot taken when the dialog opened).
  const { data: groups = [] } = useQuery({ queryKey: ["groups"], queryFn: api.listGroups });
  const { data: users = [] } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const current = groups.find((g) => g.id === group.id) ?? group;
  const memberIds = current.memberIds;
  const members = users.filter((u) => memberIds.includes(u.id));

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const invalidate = () => qc.invalidateQueries({ queryKey: ["groups"] });
  const onError = (e: unknown) => setError(e instanceof Error ? e.message : String(e));
  const add = useMutation({
    mutationFn: (userId: string) => api.addGroupMember(group.id, userId),
    onSuccess: invalidate,
    onError,
  });
  const removeMember = useMutation({
    mutationFn: (userId: string) => api.removeGroupMember(group.id, userId),
    onSuccess: invalidate,
    onError,
  });

  return (
    <div
      data-testid="group-members-backdrop"
      data-nested-dialog
      className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-4"
    >
      <div
        role="dialog"
        aria-label={t("membersTitle", { name: current.name })}
        className="flex max-h-[80vh] w-full max-w-md flex-col rounded-lg border bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="shrink-0 border-b px-4 py-3 dark:border-gray-700">
          <h2 className="text-sm font-semibold dark:text-gray-100">{t("membersTitle", { name: current.name })}</h2>
        </div>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-4">
          {error && <p className="text-xs text-red-600 dark:text-red-400">{error}</p>}

          {/* Add member: search-to-add type-ahead over all users, excluding current members. */}
          <AddMemberTypeahead users={users} excludeIds={memberIds} onAdd={(userId) => add.mutate(userId)} />

          {/* Current members. */}
          <div className="space-y-1">
            {members.length === 0 ? (
              <p className="text-xs text-gray-400 dark:text-gray-500">{t("noMembers")}</p>
            ) : (
              members.map((u) => {
                // The system group must keep at least one member (the server enforces it too).
                const lastSystemMember = current.isSystem && members.length === 1;
                return (
                  <div
                    key={u.id}
                    className="flex items-center justify-between gap-2 rounded border px-2 py-1.5 text-sm dark:border-gray-700 dark:text-gray-100"
                  >
                    <span className="min-w-0 truncate">{u.fullName || u.email}</span>
                    <button
                      type="button"
                      data-testid={`member-${group.id}-${u.id}`}
                      disabled={lastSystemMember}
                      onClick={() => removeMember.mutate(u.id)}
                      className="shrink-0 text-xs text-red-600 hover:underline disabled:opacity-40 dark:text-red-400"
                    >
                      {t("removeMember")}
                    </button>
                  </div>
                );
              })
            )}
          </div>

          {current.isSystem && (
            <p className="text-[11px] text-gray-500 dark:text-gray-400">{t("groupSystemHint")}</p>
          )}
        </div>

        <div className="shrink-0 border-t px-4 py-3 dark:border-gray-700">
          <button
            type="button"
            onClick={onClose}
            className="w-full rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </div>
  );
}
