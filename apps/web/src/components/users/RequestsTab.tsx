import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import SetupLinkBanner from "./SetupLinkBanner";
import StatusPill from "./StatusPill";
import UserAvatar from "./UserAvatar";

/// People who asked for an account and are waiting on a decision.
///
/// Purely a layout move: same `grantUser` / `denyUser`, same `Requested` filter over the same
/// `["admin-users"]` data that fed the section this replaces.
///
/// The design showed a "Requested 6 Aug, 09:12" line under each address. There is no such field - not on the
/// DTO, not on the user entity, and `IdentityUser` has no created date - so it is deferred with the rest of
/// the account facts rather than faked.
export default function RequestsTab() {
  const { t } = useTranslation("admin");
  const qc = useQueryClient();
  const { data: users = [] } = useQuery({ queryKey: ["admin-users"], queryFn: api.listUsers });
  const [error, setError] = useState<string | null>(null);
  const [grantLink, setGrantLink] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  // Granting moves the user into the default group server-side, so the group rows must refresh too.
  const refresh = () => {
    void qc.invalidateQueries({ queryKey: ["admin-users"] });
    return qc.invalidateQueries({ queryKey: ["groups"] });
  };

  const pending = users.filter((u) => u.status === "Requested");

  async function grant(id: string) {
    setError(null);
    setGrantLink(null);
    setBusy(id);
    try {
      const r = await api.grantUser(id);
      refresh();
      if (!r.emailed && r.setupUrl) setGrantLink(r.setupUrl);
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  async function deny(id: string, email: string) {
    setError(null);
    if (!window.confirm(t("confirmDeny", { email }))) return;
    setBusy(id);
    try {
      await api.denyUser(id);
      refresh();
    } catch (e) {
      setError(apiErrorMessage(e));
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="min-h-0 flex-1 overflow-y-auto">
      {(grantLink || error) && (
        <div className="space-y-2 px-[18px] py-2.5">
          {grantLink && <SetupLinkBanner url={grantLink} />}
          {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
        </div>
      )}

      {pending.length === 0 ? (
        <p className="px-[18px] py-6 text-sm text-gray-500 dark:text-gray-400">{t("requestsEmpty")}</p>
      ) : (
        <ul>
          {pending.map((u) => (
            <li
              key={u.id}
              className="flex items-center gap-2.5 border-b border-gray-100 px-3.5 py-2.5 dark:border-gray-800"
            >
              <UserAvatar user={u} />
              <span className="min-w-0 flex-1 truncate text-[13px] dark:text-gray-100">{u.email}</span>
              <StatusPill user={u} />
              <button
                type="button"
                onClick={() => void grant(u.id)}
                disabled={busy === u.id}
                className="shrink-0 rounded bg-blue-600 px-3 py-1 text-xs text-white disabled:opacity-50"
              >
                {t("grant")}
              </button>
              <button
                type="button"
                onClick={() => void deny(u.id, u.email)}
                disabled={busy === u.id}
                className="shrink-0 rounded border border-red-300 px-3 py-1 text-xs text-red-600 disabled:opacity-50 dark:border-red-800 dark:text-red-400"
              >
                {t("deny")}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
