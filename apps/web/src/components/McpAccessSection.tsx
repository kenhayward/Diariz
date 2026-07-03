import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { McpTokenCreated } from "../lib/types";

/// The Preferences "Claude / MCP access" section: shows the MCP endpoint URL, lets the user generate a
/// personal access token (shown exactly once, with a ready-to-paste Claude config snippet), and lists /
/// revokes existing tokens. The secret is never retrievable after generation — only a short prefix is stored.
export default function McpAccessSection() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: tokens } = useQuery({ queryKey: ["mcp-tokens"], queryFn: api.listMcpTokens });
  const { data: connections } = useQuery({ queryKey: ["oauth-connections"], queryFn: api.listOAuthConnections });

  const [name, setName] = useState("");
  const [created, setCreated] = useState<McpTokenCreated | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const mcpUrl = `${window.location.origin}/mcp`;
  // Claude Desktop's config only accepts stdio servers, so bridge to our HTTP endpoint with mcp-remote.
  // The header goes in via an env var (referenced as ${AUTH}) so mcp-remote doesn't split it on the space
  // in "Bearer <token>".
  const configSnippet = JSON.stringify(
    {
      mcpServers: {
        diariz: {
          command: "npx",
          args: ["-y", "mcp-remote", mcpUrl, "--header", "Authorization:${AUTH}"],
          env: { AUTH: `Bearer ${created?.token ?? "<your-token>"}` },
        },
      },
    },
    null,
    2,
  );

  function copy(text: string) {
    void navigator.clipboard?.writeText(text);
  }

  async function generate() {
    setError(null);
    setBusy(true);
    try {
      const tok = await api.createMcpToken(name.trim() || t("mcpDefaultName"));
      setCreated(tok);
      setName("");
      qc.invalidateQueries({ queryKey: ["mcp-tokens"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("mcpGenerateError")));
    } finally {
      setBusy(false);
    }
  }

  async function revoke(id: string) {
    setError(null);
    try {
      await api.revokeMcpToken(id);
      qc.invalidateQueries({ queryKey: ["mcp-tokens"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("mcpRevokeError")));
    }
  }

  async function disconnect(id: string) {
    setError(null);
    try {
      await api.revokeOAuthConnection(id);
      qc.invalidateQueries({ queryKey: ["oauth-connections"] });
    } catch (e) {
      setError(apiErrorMessage(e, t("mcpRevokeError")));
    }
  }

  const btn =
    "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="border-t pt-3 dark:border-gray-700">
      <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("mcpAccess")}</span>
      <p className="text-xs text-gray-400 dark:text-gray-500">{t("mcpIntro")}</p>

      <div className="mt-2 flex items-center gap-2">
        <code className="flex-1 truncate rounded bg-gray-100 px-2 py-1 text-xs dark:bg-gray-800 dark:text-gray-200">
          {mcpUrl}
        </code>
        <button type="button" onClick={() => copy(mcpUrl)} className={btn}>
          {t("mcpCopyUrl")}
        </button>
      </div>

      <div className="mt-2 flex gap-2">
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={t("mcpTokenNamePlaceholder")}
          aria-label={t("mcpTokenNamePlaceholder")}
          className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <button type="button" onClick={generate} disabled={busy} className={btn}>
          {t("mcpGenerate")}
        </button>
      </div>

      {created && (
        <div className="mt-2 rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20">
          <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{t("mcpTokenOnce")}</p>
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">
              {created.token}
            </code>
            <button type="button" onClick={() => copy(created.token)} className={btn}>
              {t("mcpCopyToken")}
            </button>
          </div>
          <details className="mt-2">
            <summary className="cursor-pointer text-xs text-gray-600 dark:text-gray-300">{t("mcpShowConfig")}</summary>
            <pre className="mt-1 overflow-x-auto rounded bg-white p-2 text-[11px] leading-snug dark:bg-gray-900 dark:text-gray-200">
              {configSnippet}
            </pre>
            <button type="button" onClick={() => copy(configSnippet)} className={`mt-1 ${btn}`}>
              {t("mcpCopyConfig")}
            </button>
            <p className="mt-1 text-[11px] leading-snug text-gray-400 dark:text-gray-500">{t("mcpDesktopNote")}</p>
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
              {tk.lastUsedAt ? t("mcpLastUsed") : t("mcpNeverUsed")}
            </span>
            <button
              type="button"
              onClick={() => revoke(tk.id)}
              className="shrink-0 text-red-600 hover:underline dark:text-red-400"
            >
              {t("mcpRevoke")}
            </button>
          </li>
        ))}
        {tokens?.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("mcpNoTokens")}</li>}
      </ul>

      {/* OAuth connections: clients (e.g. the claude.ai web connector) the user signed in to grant access. */}
      <div className="mt-3 border-t pt-2 dark:border-gray-700">
        <span className="mb-1 block text-sm text-gray-600 dark:text-gray-300">{t("mcpConnections")}</span>
        <p className="text-xs text-gray-400 dark:text-gray-500">{t("mcpConnectionsIntro")}</p>
        <ul className="mt-1 space-y-1">
          {connections?.map((c) => (
            <li
              key={c.id}
              className="flex items-center justify-between gap-2 text-xs text-gray-600 dark:text-gray-300"
            >
              <span className="truncate">
                {c.clientName}
                {c.connectedAt && ` · ${t("mcpConnectedOn", { date: new Date(c.connectedAt).toLocaleDateString() })}`}
              </span>
              <button
                type="button"
                onClick={() => disconnect(c.id)}
                className="shrink-0 text-red-600 hover:underline dark:text-red-400"
              >
                {t("mcpDisconnect")}
              </button>
            </li>
          ))}
          {connections?.length === 0 && (
            <li className="text-xs text-gray-400 dark:text-gray-500">{t("mcpNoConnections")}</li>
          )}
        </ul>
      </div>

      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
    </div>
  );
}
