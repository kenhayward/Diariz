import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { api } from "../lib/api";
import HelpButton from "./HelpButton";
import UsersTab from "./users/UsersTab";
import GroupsTab from "./users/GroupsTab";
import RequestsTab from "./users/RequestsTab";

type Tab = "users" | "groups" | "requests";

/// The Users & access console: the shell only - a title, three tabs, and whichever tab is showing.
///
/// It used to be one component holding a users table, an access-requests section stacked above it, and the
/// groups matrix. Each tab now owns its own queries (react-query dedupes the shared ones), which is why
/// nothing is drilled through here except `onClose`.
///
/// Requests were promoted from a section to a tab. On a busy platform they pushed the user list off screen,
/// and when there were none they left a heading over nothing. As a tab with a count badge they are visible
/// when they exist and silent when they do not.
export default function ManageUsersModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("admin");
  const [tab, setTab] = useState<Tab>("users");
  // Only for the badge. The Requests tab loads the same query itself.
  const { data: users = [] } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const pendingCount = users.filter((u) => u.status === "Requested").length;

  useEffect(() => {
    // Escape closes - but defer to a nested dialog (the Edit group dialog) when one is open, so Escape
    // dismisses that first rather than the whole console.
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !document.querySelector("[data-nested-dialog]")) onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const tabs: { id: Tab; label: string; badge?: number }[] = [
    { id: "users", label: t("usersTab") },
    { id: "groups", label: t("groupsTab") },
    { id: "requests", label: t("requestsTab"), badge: pendingCount },
  ];

  return (
    // The backdrop does NOT close on click (the ✕ or Escape only) - prevents accidental dismissal mid-edit.
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      {/* Fixed height so switching tabs never resizes the console. Wider than the old modal because the
          detail pane needs the room; no padding here, since the list and the pane run to the edges. */}
      <div
        role="dialog"
        aria-label={t("title")}
        className="flex h-[85vh] w-full max-w-6xl flex-col overflow-hidden rounded-[10px] border border-gray-200 bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex shrink-0 items-center justify-between px-5 pt-4">
          <h2 className="text-base font-semibold dark:text-gray-100">{t("title")}</h2>
          <div className="flex items-center gap-1">
            <HelpButton topic="users-and-groups" />
            {/* Replaces the old footer Close, which cost the list a whole row on a full-height console. */}
            <button
              type="button"
              onClick={onClose}
              aria-label={t("common:close")}
              className="rounded px-2 py-1 text-lg leading-none text-gray-400 hover:bg-gray-100 hover:text-gray-700 dark:hover:bg-gray-800 dark:hover:text-gray-200"
            >
              ✕
            </button>
          </div>
        </div>

        <div role="tablist" className="flex shrink-0 gap-1 border-b border-gray-200 px-5 dark:border-gray-700">
          {tabs.map((x) => (
            <button
              key={x.id}
              type="button"
              role="tab"
              aria-selected={tab === x.id}
              onClick={() => setTab(x.id)}
              className={`flex items-center gap-1.5 border-b-2 px-3 py-2 text-sm ${
                tab === x.id
                  ? "border-blue-600 font-medium text-blue-700 dark:text-blue-300"
                  : "border-transparent text-gray-500 hover:text-gray-800 dark:text-gray-400 dark:hover:text-gray-200"
              }`}
            >
              {x.label}
              {/* Absent at zero rather than showing a 0 - an empty queue is not news. */}
              {x.badge ? (
                <span className="rounded-full bg-amber-100 px-1.5 text-[10px] font-semibold text-amber-800 dark:bg-amber-700/80 dark:text-amber-100">
                  {x.badge}
                </span>
              ) : null}
            </button>
          ))}
        </div>

        <div className="flex min-h-0 min-w-0 flex-1 flex-col">
          {tab === "users" && <UsersTab />}
          {tab === "groups" && <GroupsTab />}
          {tab === "requests" && <RequestsTab />}
        </div>
      </div>
    </div>
  );
}
