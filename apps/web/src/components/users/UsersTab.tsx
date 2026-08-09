import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "../../auth";
import { api, apiErrorMessage } from "../../lib/api";
import { SearchIcon } from "../icons";
import SetupLinkBanner from "./SetupLinkBanner";
import UserDetail from "./UserDetail";
import UserListRow from "./UserListRow";
import { countByStatus, filterUsers, type StatusFilter } from "./userFilters";

const label = "text-[10px] font-medium uppercase tracking-[.06em] text-gray-400 dark:text-gray-600";

/// The Users tab: find someone on the left, do everything to them on the right.
///
/// Filtering is entirely client-side over the already-loaded `["admin-users"]`. That is a deliberate scale
/// choice - tens to low hundreds of accounts - and it is what lets the chips carry live counts. Somewhere
/// around 500 users this wants server-side search and pagination instead.
export default function UsersTab() {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { email: myEmail } = useAuth();
  const { data: users = [], isLoading } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const { data: groups = [] } = useQuery({ queryKey: ["groups"], queryFn: api.listGroups });
  const { data: platform } = useQuery({ queryKey: ["platform-settings"], queryFn: api.getPlatformSettings });

  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<StatusFilter>("all");
  // The id, not the user: a grant, a quota save or a group change refetches the list, and holding the object
  // would pin a stale copy.
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [grantLink, setGrantLink] = useState<string | null>(null);
  const [newEmail, setNewEmail] = useState("");
  const [newName, setNewName] = useState("");
  const [adding, setAdding] = useState(false);

  const counts = countByStatus(users);
  const shown = filterUsers(users, search, status);
  const selected = users.find((u) => u.id === selectedId) ?? null;

  async function addUser(e: React.FormEvent) {
    e.preventDefault();
    const email = newEmail.trim();
    if (!email || adding) return;
    setAdding(true);
    setError(null);
    setGrantLink(null);
    try {
      const r = await api.addUser(email, newName.trim() || undefined);
      await qc.invalidateQueries({ queryKey: ["admin-users"] });
      void qc.invalidateQueries({ queryKey: ["groups"] });
      setNewEmail("");
      setNewName("");
      if (!r.emailed && r.setupUrl) setGrantLink(r.setupUrl);
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setAdding(false);
    }
  }

  const chips: { id: StatusFilter; text: string }[] = [
    { id: "all", text: t("filterAll", { n: counts.all }) },
    { id: "active", text: t("filterActive", { n: counts.active }) },
    { id: "invited", text: t("filterInvited", { n: counts.invited }) },
    { id: "disabled", text: t("filterDisabled", { n: counts.disabled }) },
  ];

  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col">
      {/* Find. The counts are part of each chip's label so "how many are half-onboarded?" is answered
          without a click. */}
      <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-gray-200 px-[18px] py-2.5 dark:border-gray-700">
        <div className="relative">
          <span className="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-gray-400">
            <SearchIcon size={13} />
          </span>
          <input
            type="search"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder={t("searchUsers")}
            aria-label={t("searchUsers")}
            className="w-[230px] rounded border border-gray-300 py-1.5 pl-7 pr-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
        </div>
        {chips.map((c) => (
          <button
            key={c.id}
            type="button"
            aria-pressed={status === c.id}
            onClick={() => setStatus(c.id)}
            className={`rounded-full px-2.5 py-[3px] text-xs ${
              status === c.id
                ? "bg-blue-50 text-blue-800 ring-1 ring-blue-200 dark:bg-blue-500/[0.18] dark:text-blue-100 dark:ring-blue-400/40"
                : "text-gray-500 ring-1 ring-gray-200 hover:text-gray-800 dark:text-gray-400 dark:ring-gray-700 dark:hover:text-gray-200"
            }`}
          >
            {c.text}
          </button>
        ))}
      </div>

      {/* Add. */}
      <form
        onSubmit={addUser}
        className="flex shrink-0 flex-wrap items-center gap-2 border-b border-gray-200 px-[18px] py-2.5 dark:border-gray-700"
      >
        <span className={label}>{t("addAUser")}</span>
        <input
          type="text"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          placeholder={t("fullName")}
          aria-label={t("newUserNameAria")}
          className="min-w-0 flex-1 rounded border border-gray-300 px-2 py-1.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <input
          type="email"
          value={newEmail}
          onChange={(e) => setNewEmail(e.target.value)}
          placeholder="new.user@example.com"
          aria-label={t("newUserEmailAria")}
          className="min-w-0 flex-1 rounded border border-gray-300 px-2 py-1.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <button
          type="submit"
          disabled={adding || !newEmail.trim()}
          className="shrink-0 rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {adding ? t("adding") : t("addUser")}
        </button>
      </form>

      {(grantLink || error) && (
        <div className="shrink-0 space-y-2 px-[18px] py-2.5">
          {grantLink && <SetupLinkBanner url={grantLink} />}
          {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
        </div>
      )}

      <div className="flex min-h-0 min-w-0 flex-1">
        <div className="flex min-h-0 min-w-0 flex-1 flex-col">
          <div className="flex shrink-0 items-center gap-2.5 border-b border-gray-100 px-3.5 py-1.5 dark:border-gray-800">
            <span className="w-6 shrink-0" />
            <span className={`min-w-0 flex-1 ${label}`}>{t("colUser")}</span>
            <span className={`w-[170px] shrink-0 ${label}`}>{t("colGroups")}</span>
            <span className={`w-[120px] shrink-0 ${label}`}>{t("colStorage")}</span>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto">
            {isLoading ? (
              <p className="px-3.5 py-6 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
            ) : shown.length === 0 ? (
              <p className="px-3.5 py-6 text-sm text-gray-500 dark:text-gray-400">{t("noUsersMatch")}</p>
            ) : (
              shown.map((u) => (
                <UserListRow
                  key={u.id}
                  user={u}
                  groupNames={groups.filter((g) => g.memberIds.includes(u.id)).map((g) => g.name)}
                  selected={u.id === selectedId}
                  onSelect={() => setSelectedId(u.id)}
                />
              ))
            )}
          </div>
        </div>

        {selected ? (
          <UserDetail
            user={selected}
            groups={groups}
            isSelf={!!myEmail && selected.email === myEmail}
            maxQuotaBytes={platform?.maxQuotaBytes ?? null}
            // Deleting the selected account must empty the pane, not leave it pointing at a ghost.
            onDeleted={() => setSelectedId(null)}
          />
        ) : (
          <div className="w-[340px] shrink-0 border-l border-gray-200 bg-gray-50 p-4 text-[11px] text-gray-500 dark:border-gray-700 dark:bg-gray-950/40 dark:text-gray-400">
            {t("pickUser")}
          </div>
        )}
      </div>
    </div>
  );
}
