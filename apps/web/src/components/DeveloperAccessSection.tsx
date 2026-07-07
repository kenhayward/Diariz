import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { api, apiErrorMessage } from "../lib/api";
import type { ApiTokenCreated } from "../lib/types";

/// The Preferences "Developers / API access" section: shows the API base URL, generates a personal API token
/// (shown exactly once, with a ready-to-run curl example) and lists / revokes existing tokens. Shown only when
/// the platform has API access enabled (the parent gates on profile.apiAccessEnabled). The secret is never
/// retrievable after generation - only a short prefix is stored.
export default function DeveloperAccessSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: tokens } = useQuery({ queryKey: ["api-tokens"], queryFn: api.listApiTokens });

  const [name, setName] = useState("");
  const [created, setCreated] = useState<ApiTokenCreated | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const baseUrl = `${window.location.origin}/api`;
  const example = `curl -H "Authorization: Bearer ${created?.token ?? "<your-token>"}" ${baseUrl}/recordings`;

  const copy = (text: string) => void navigator.clipboard?.writeText(text);

  async function generate() {
    setError(null);
    setBusy(true);
    try {
      const tok = await api.createApiToken(name.trim() || t("apiDefaultName"));
      setCreated(tok);
      setName("");
      qc.invalidateQueries({ queryKey: ["api-tokens"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("apiGenerateError")));
    } finally {
      setBusy(false);
    }
  }

  async function revoke(id: string) {
    setError(null);
    try {
      await api.revokeApiToken(id);
      qc.invalidateQueries({ queryKey: ["api-tokens"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("apiRevokeError")));
    }
  }

  const btn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("apiAccess")}</span>
      <p className="text-xs text-gray-400 dark:text-gray-500">{t("apiIntro")}</p>

      <div className="mt-2 flex items-center gap-2">
        <code className="flex-1 truncate rounded bg-gray-100 px-2 py-1 text-xs dark:bg-gray-800 dark:text-gray-200">
          {baseUrl}
        </code>
        <button type="button" onClick={() => copy(baseUrl)} className={btn}>
          {t("apiCopyUrl")}
        </button>
        <Link to="/developers/api" className={btn}>
          {t("apiViewReference")}
        </Link>
      </div>

      <div className="mt-2 flex gap-2">
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={t("apiTokenNamePlaceholder")}
          aria-label={t("apiTokenNamePlaceholder")}
          className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <button type="button" onClick={generate} disabled={busy} className={btn}>
          {t("apiGenerate")}
        </button>
      </div>

      {created && (
        <div className="mt-2 rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
          <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{t("apiTokenOnce")}</p>
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">
              {created.token}
            </code>
            <button type="button" onClick={() => copy(created.token)} className={btn}>
              {t("apiCopyToken")}
            </button>
          </div>
          <details className="mt-2">
            <summary className="cursor-pointer text-xs text-gray-600 dark:text-gray-300">{t("apiShowExample")}</summary>
            <pre className="mt-1 overflow-x-auto rounded bg-white p-2 text-[11px] leading-snug dark:bg-gray-900 dark:text-gray-200">
              {example}
            </pre>
            <button type="button" onClick={() => copy(example)} className={`mt-1 ${btn}`}>
              {t("apiCopyExample")}
            </button>
          </details>
        </div>
      )}

      <ul className="mt-2 space-y-1">
        {tokens?.map((tk) => (
          <li
            key={tk.id}
            className="flex items-center justify-between gap-2 text-xs text-gray-600 dark:text-gray-300"
          >
            <span className="truncate">
              {tk.name} · <code className="text-gray-500 dark:text-gray-400">{tk.prefix}…</code> ·{" "}
              {tk.lastUsedAt ? t("apiLastUsed") : t("apiNeverUsed")}
            </span>
            <button
              type="button"
              onClick={() => revoke(tk.id)}
              className="shrink-0 text-red-600 hover:underline dark:text-red-400"
            >
              {t("apiRevoke")}
            </button>
          </li>
        ))}
        {tokens?.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("apiNoTokens")}</li>}
      </ul>

      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
    </div>
  );
}
