import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import { bytesToGb, formatBytes, gbToBytes } from "../lib/format";
import type { AdminUser } from "../lib/types";

/// Admin-only user management: grant/deny access requests, change account type, enable/disable, and
/// delete users. Destructive actions are hidden for the Platform Administrator and the current user
/// (the server enforces this too).
export default function ManageUsersModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { email: myEmail } = useAuth();
  const { data: users = [], isLoading } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const { data: platform } = useQuery({ queryKey: ["platform-settings"], queryFn: api.getPlatformSettings });
  const [error, setError] = useState<string | null>(null);
  const [grantLink, setGrantLink] = useState<string | null>(null);
  const [newEmail, setNewEmail] = useState("");
  const [newName, setNewName] = useState("");
  const [adding, setAdding] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const refresh = () => qc.invalidateQueries({ queryKey: ["admin-users"] });
  const run = (fn: () => Promise<unknown>) => async () => {
    setError(null);
    try {
      await fn();
      refresh();
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  };

  async function grant(id: string) {
    setError(null);
    setGrantLink(null);
    try {
      const r = await api.grantUser(id);
      refresh();
      if (!r.emailed && r.setupUrl) setGrantLink(r.setupUrl);
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }

  async function addUser(e: React.FormEvent) {
    e.preventDefault();
    const email = newEmail.trim();
    if (!email || adding) return;
    setAdding(true);
    setError(null);
    setGrantLink(null);
    try {
      const r = await api.addUser(email, newName.trim() || undefined);
      refresh();
      setNewEmail("");
      setNewName("");
      if (!r.emailed && r.setupUrl) setGrantLink(r.setupUrl);
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setAdding(false);
    }
  }

  const pending = users.filter((u) => u.status === "Requested");
  const others = users.filter((u) => u.status !== "Requested");

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("title")}
        className="flex max-h-[85vh] w-full max-w-4xl flex-col rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Pinned header: title, add-user form, and access requests stay put while the user table scrolls. */}
        <h2 className="mb-3 shrink-0 text-base font-semibold dark:text-gray-100">{t("title")}</h2>

        {/* Add a user by name + email — creates the account and emails them a setup link (or shows it below). */}
        <form onSubmit={addUser} className="mb-3 flex shrink-0 flex-wrap items-center gap-2">
          <input
            type="text"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            placeholder={t("fullName")}
            aria-label={t("newUserNameAria")}
            className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <input
            type="email"
            value={newEmail}
            onChange={(e) => setNewEmail(e.target.value)}
            placeholder="new.user@example.com"
            aria-label={t("newUserEmailAria")}
            className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <button
            type="submit"
            disabled={adding || !newEmail.trim()}
            className="shrink-0 rounded bg-blue-600 px-3 py-1 text-sm text-white disabled:opacity-50"
          >
            {adding ? t("adding") : t("addUser")}
          </button>
        </form>

        {grantLink && (
          <div className="mb-3 shrink-0 rounded border border-blue-300 bg-blue-50 p-2 text-xs dark:border-blue-800 dark:bg-blue-950/40">
            <p className="mb-1 font-medium text-blue-800 dark:text-blue-300">{t("grantLinkMsg")}</p>
            <code className="block break-all text-blue-700 dark:text-blue-300">{grantLink}</code>
          </div>
        )}
        {error && <p className="mb-2 shrink-0 text-sm text-red-600 dark:text-red-400">{error}</p>}
        {isLoading && <p className="shrink-0 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>}

        {pending.length > 0 && (
          <section className="mb-4 shrink-0">
            <h3 className="mb-1 text-xs font-bold uppercase tracking-wide text-gray-400">{t("accessRequests")}</h3>
            <ul className="divide-y dark:divide-gray-800">
              {pending.map((u) => (
                <li key={u.id} className="flex items-center justify-between gap-2 py-2 text-sm dark:text-gray-200">
                  <span className="flex min-w-0 items-center gap-2">
                    <span className="truncate">{u.email}</span>
                    <StatusPill status={u.status} />
                  </span>
                  <span className="flex shrink-0 gap-2">
                    <button onClick={() => grant(u.id)} className="rounded bg-blue-600 px-2 py-1 text-xs text-white">
                      {t("grant")}
                    </button>
                    <button
                      onClick={run(async () => {
                        if (window.confirm(t("confirmDeny", { email: u.email }))) await api.denyUser(u.id);
                      })}
                      className="rounded border px-2 py-1 text-xs dark:border-gray-700"
                    >
                      {t("deny")}
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          </section>
        )}

        {/* Users table — the only part that scrolls, so it stays usable with many users. Sticky header row. */}
        <div className="min-h-0 flex-1 overflow-auto">
          <table className="w-full text-sm">
            <thead className="sticky top-0 z-10 bg-white text-left text-xs text-gray-400 dark:bg-gray-900 dark:text-gray-500">
              <tr>
                <th className="py-1 pr-2 font-medium">{t("colUser")}</th>
                <th className="py-1 pr-2 font-medium">{t("colType")}</th>
                <th className="py-1 pr-2 font-medium">{t("colStatus")}</th>
                <th className="py-1 pr-2 font-medium">{t("colStorage")}</th>
                <th className="py-1 font-medium">{t("colActions")}</th>
              </tr>
            </thead>
            <tbody className="divide-y dark:divide-gray-800">
              {others.map((u) => (
                <UserRow
                  key={u.id}
                  u={u}
                  isSelf={!!myEmail && u.email === myEmail}
                  maxQuotaBytes={platform?.maxQuotaBytes ?? null}
                  onPromote={run(() => api.setUserRole(u.id, "Administrator"))}
                  onDemote={run(() => api.setUserRole(u.id, "Standard"))}
                  onSetEnabled={(v) => run(() => api.setUserEnabled(u.id, v))()}
                  onSetQuota={(bytes) => run(() => api.setUserQuota(u.id, bytes))()}
                  onDelete={run(async () => {
                    if (window.confirm(t("confirmDeleteUser", { email: u.email }))) await api.deleteUser(u.id);
                  })}
                />
              ))}
            </tbody>
          </table>
        </div>

        <div className="mt-4 flex shrink-0 justify-end">
          <button
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:close")}
          </button>
        </div>
      </div>
    </div>
  );
}

/// Onboarding status pill: Requested (awaiting grant) → Awaiting setup (granted) → Active.
function StatusPill({ status }: { status: AdminUser["status"] }) {
  const { t } = useTranslation("admin");
  const styles: Record<AdminUser["status"], string> = {
    Requested: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
    Invited: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
    Active: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  };
  const labels: Record<AdminUser["status"], string> = {
    Requested: t("statusRequested"),
    Invited: t("statusAwaitingSetup"),
    Active: t("statusActive"),
  };
  return <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] ${styles[status]}`}>{labels[status]}</span>;
}

function UserRow({
  u,
  isSelf,
  maxQuotaBytes,
  onPromote,
  onDemote,
  onSetEnabled,
  onSetQuota,
  onDelete,
}: {
  u: AdminUser;
  isSelf: boolean;
  maxQuotaBytes: number | null;
  onPromote: () => void;
  onDemote: () => void;
  onSetEnabled: (v: boolean) => void;
  onSetQuota: (bytes: number) => void;
  onDelete: () => void;
}) {
  const { t } = useTranslation("admin");
  const isPlatform = u.accountType === "PlatformAdministrator";
  const protectedRow = isPlatform || isSelf; // no destructive/role actions
  const [editingQuota, setEditingQuota] = useState(false);
  const [quotaGb, setQuotaGb] = useState(String(bytesToGb(u.quotaBytes)));
  const maxGb = maxQuotaBytes != null ? bytesToGb(maxQuotaBytes) : undefined;
  return (
    <tr className="align-top dark:text-gray-200">
      {/* User: name on top, email beneath (only when there's a distinct name), + disabled badge. */}
      <td className="py-2 pr-2">
        <div className="flex items-center gap-2">
          <span className="truncate font-medium">{u.fullName || u.email}</span>
          {!u.isEnabled && (
            <span className="shrink-0 rounded bg-red-100 px-1.5 py-0.5 text-[10px] text-red-800 dark:bg-red-900/40 dark:text-red-300">
              {t("disabled")}
            </span>
          )}
        </div>
        {u.fullName && (
          <div className="truncate text-xs text-gray-500 dark:text-gray-400">{u.email}</div>
        )}
      </td>
      <td className="py-2 pr-2 text-gray-600 dark:text-gray-300">{u.accountType}</td>
      <td className="py-2 pr-2">
        <StatusPill status={u.status} />
      </td>
      <td className="py-2 pr-2 text-xs text-gray-500 dark:text-gray-400">
        {editingQuota ? (
          <span className="flex items-center gap-1.5">
            <span>{t("quota")}</span>
            <input
              type="number"
              min={0}
              step={0.5}
              max={maxGb}
              value={quotaGb}
              onChange={(e) => setQuotaGb(e.target.value)}
              aria-label={t("quotaForAria", { email: u.email })}
              className="w-20 rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
            <span>{t("gb")}</span>
            <button
              onClick={() => {
                onSetQuota(gbToBytes(Number(quotaGb)));
                setEditingQuota(false);
              }}
              className="rounded border px-1.5 py-0.5 text-[11px] dark:border-gray-700"
            >
              {t("common:save")}
            </button>
            <button onClick={() => setEditingQuota(false)} className="text-[11px] hover:underline">
              {t("common:cancel")}
            </button>
          </span>
        ) : (
          <span className="flex flex-wrap items-center gap-x-1.5">
            <span className="whitespace-nowrap">
              {t("storageLine", { used: formatBytes(u.usedBytes), total: formatBytes(u.quotaBytes) })}
            </span>
            <button
              onClick={() => {
                setQuotaGb(String(bytesToGb(u.quotaBytes)));
                setEditingQuota(true);
              }}
              className="text-[11px] text-blue-600 hover:underline dark:text-blue-400"
            >
              {t("editQuota")}
            </button>
          </span>
        )}
      </td>
      <td className="py-2">
        {!protectedRow && (
          <span className="flex flex-wrap gap-1.5">
            {u.accountType === "Standard" ? (
              <button onClick={onPromote} className="rounded border px-2 py-1 text-xs dark:border-gray-700">
                {t("makeAdmin")}
              </button>
            ) : (
              <button onClick={onDemote} className="rounded border px-2 py-1 text-xs dark:border-gray-700">
                {t("makeStandard")}
              </button>
            )}
            <button
              onClick={() => onSetEnabled(!u.isEnabled)}
              className="rounded border px-2 py-1 text-xs dark:border-gray-700"
            >
              {u.isEnabled ? t("disable") : t("enable")}
            </button>
            <button onClick={onDelete} className="rounded border border-red-300 px-2 py-1 text-xs text-red-600 dark:border-red-800 dark:text-red-400">
              {t("delete")}
            </button>
          </span>
        )}
      </td>
    </tr>
  );
}
