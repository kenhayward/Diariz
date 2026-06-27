import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import type { AdminUser } from "../lib/types";

/// Admin-only user management: grant/deny access requests, change account type, enable/disable, and
/// delete users. Destructive actions are hidden for the Platform Administrator and the current user
/// (the server enforces this too).
export default function ManageUsersModal({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const { email: myEmail } = useAuth();
  const { data: users = [], isLoading } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const [error, setError] = useState<string | null>(null);
  const [grantLink, setGrantLink] = useState<string | null>(null);
  const [newEmail, setNewEmail] = useState("");
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
      const r = await api.addUser(email);
      refresh();
      setNewEmail("");
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
        aria-label="Manage users"
        className="max-h-[85vh] w-full max-w-2xl overflow-y-auto rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">Manage users</h2>

        {/* Add a user by email — creates the account and emails them a setup link (or shows it below). */}
        <form onSubmit={addUser} className="mb-3 flex items-center gap-2">
          <input
            type="email"
            value={newEmail}
            onChange={(e) => setNewEmail(e.target.value)}
            placeholder="new.user@example.com"
            aria-label="New user email"
            className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <button
            type="submit"
            disabled={adding || !newEmail.trim()}
            className="shrink-0 rounded bg-blue-600 px-3 py-1 text-sm text-white disabled:opacity-50"
          >
            {adding ? "Adding…" : "Add user"}
          </button>
        </form>

        {grantLink && (
          <div className="mb-3 rounded border border-blue-300 bg-blue-50 p-2 text-xs dark:border-blue-800 dark:bg-blue-950/40">
            <p className="mb-1 font-medium text-blue-800 dark:text-blue-300">
              Email isn't configured — share this one-time setup link with the user:
            </p>
            <code className="block break-all text-blue-700 dark:text-blue-300">{grantLink}</code>
          </div>
        )}
        {error && <p className="mb-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
        {isLoading && <p className="text-sm text-gray-500 dark:text-gray-400">Loading…</p>}

        {pending.length > 0 && (
          <section className="mb-4">
            <h3 className="mb-1 text-xs font-bold uppercase tracking-wide text-gray-400">Access requests</h3>
            <ul className="divide-y dark:divide-gray-800">
              {pending.map((u) => (
                <li key={u.id} className="flex items-center justify-between gap-2 py-2 text-sm dark:text-gray-200">
                  <span className="flex min-w-0 items-center gap-2">
                    <span className="truncate">{u.email}</span>
                    <StatusPill status={u.status} />
                  </span>
                  <span className="flex shrink-0 gap-2">
                    <button onClick={() => grant(u.id)} className="rounded bg-blue-600 px-2 py-1 text-xs text-white">
                      Grant
                    </button>
                    <button
                      onClick={run(async () => {
                        if (window.confirm(`Deny access for ${u.email}?`)) await api.denyUser(u.id);
                      })}
                      className="rounded border px-2 py-1 text-xs dark:border-gray-700"
                    >
                      Deny
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          </section>
        )}

        <section>
          <h3 className="mb-1 text-xs font-bold uppercase tracking-wide text-gray-400">Users</h3>
          <ul className="divide-y dark:divide-gray-800">
            {others.map((u) => (
              <UserRow
                key={u.id}
                u={u}
                isSelf={!!myEmail && u.email === myEmail}
                onPromote={run(() => api.setUserRole(u.id, "Administrator"))}
                onDemote={run(() => api.setUserRole(u.id, "Standard"))}
                onSetEnabled={(v) => run(() => api.setUserEnabled(u.id, v))()}
                onDelete={run(async () => {
                  if (window.confirm(`Delete ${u.email}? This removes all their recordings.`)) await api.deleteUser(u.id);
                })}
              />
            ))}
          </ul>
        </section>

        <div className="mt-4 flex justify-end">
          <button
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

/// Onboarding status pill: Requested (awaiting grant) → Awaiting setup (granted) → Active.
function StatusPill({ status }: { status: AdminUser["status"] }) {
  const styles: Record<AdminUser["status"], string> = {
    Requested: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
    Invited: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
    Active: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  };
  const label = status === "Invited" ? "Awaiting setup" : status;
  return <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] ${styles[status]}`}>{label}</span>;
}

function UserRow({
  u,
  isSelf,
  onPromote,
  onDemote,
  onSetEnabled,
  onDelete,
}: {
  u: AdminUser;
  isSelf: boolean;
  onPromote: () => void;
  onDemote: () => void;
  onSetEnabled: (v: boolean) => void;
  onDelete: () => void;
}) {
  const isPlatform = u.accountType === "PlatformAdministrator";
  const protectedRow = isPlatform || isSelf; // no destructive/role actions
  return (
    <li className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm dark:text-gray-200">
      <span className="min-w-0">
        <span className="flex items-center gap-2">
          <span className="truncate font-medium">{u.fullName || u.email}</span>
          <StatusPill status={u.status} />
          {!u.isEnabled && (
            <span className="shrink-0 rounded bg-red-100 px-1.5 py-0.5 text-[10px] text-red-800 dark:bg-red-900/40 dark:text-red-300">
              disabled
            </span>
          )}
        </span>
        <span className="block truncate text-xs text-gray-500 dark:text-gray-400">
          {u.email} · {u.accountType}
        </span>
      </span>
      {!protectedRow && (
        <span className="flex shrink-0 flex-wrap gap-1.5">
          {u.accountType === "Standard" ? (
            <button onClick={onPromote} className="rounded border px-2 py-1 text-xs dark:border-gray-700">
              Make admin
            </button>
          ) : (
            <button onClick={onDemote} className="rounded border px-2 py-1 text-xs dark:border-gray-700">
              Make standard
            </button>
          )}
          <button
            onClick={() => onSetEnabled(!u.isEnabled)}
            className="rounded border px-2 py-1 text-xs dark:border-gray-700"
          >
            {u.isEnabled ? "Disable" : "Enable"}
          </button>
          <button onClick={onDelete} className="rounded border border-red-300 px-2 py-1 text-xs text-red-600 dark:border-red-800 dark:text-red-400">
            Delete
          </button>
        </span>
      )}
    </li>
  );
}
