import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { GlobeIcon } from "../icons";
import HelpButton from "../HelpButton";
import SourceCard, { cardBtn } from "../SourceCard";
import TokenDialog from "./TokenDialog";

const TINT = "#2563eb";
const TINT_DARK = "#93c5fd";

/// MCP access: the endpoint an AI assistant connects to, the personal tokens that authorise it, and the
/// apps that signed in through Diariz's OAuth flow instead of using a token.
///
/// Named for the protocol, not for one client. Claude appears in the sub-line as an example and in the
/// config snippet, because that snippet really is Claude Desktop's file format - but the capability is
/// MCP, and naming the section after a single client made three other clients look unsupported.
export default function McpCard() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  // Older servers omit the flag entirely and MCP defaults to on, so only an explicit false switches off -
  // but wait for the profile before asking for anything, or a switched-off platform still gets one round
  // trip for tokens it will refuse.
  const enabled = profile != null && profile.mcpAccessEnabled !== false;

  const { data: tokens } = useQuery({ queryKey: ["mcp-tokens"], queryFn: api.listMcpTokens, enabled });
  const { data: connections } = useQuery({
    queryKey: ["oauth-connections"],
    queryFn: api.listOAuthConnections,
    enabled,
  });

  const [dialog, setDialog] = useState(false);
  const [created, setCreated] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [dialogError, setDialogError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const mcpUrl = `${window.location.origin}/mcp`;

  // Claude Desktop's config only accepts stdio servers, so bridge to our HTTP endpoint with mcp-remote.
  // The header goes in via an env var (referenced as ${AUTH}) so mcp-remote doesn't split it on the space
  // in "Bearer <token>".
  const configFor = (token: string) =>
    JSON.stringify(
      {
        mcpServers: {
          diariz: {
            command: "npx",
            args: ["-y", "mcp-remote", mcpUrl, "--header", "Authorization:${AUTH}"],
            env: { AUTH: `Bearer ${token}` },
          },
        },
      },
      null,
      2,
    );

  async function generate(name: string) {
    setDialogError(null);
    setBusy(true);
    try {
      const tok = await api.createMcpToken(name || t("mcpDefaultName"));
      setCreated(tok.token);
      qc.invalidateQueries({ queryKey: ["mcp-tokens"] });
    } catch (e) {
      setDialogError(apiErrorMessage(e, t("mcpGenerateError")));
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

  const counts = [
    tokens?.length ? t("integrationsTokenCount", { count: tokens.length }) : null,
    connections?.length ? t("integrationsAppCount", { count: connections.length }) : null,
  ].filter(Boolean).join(" · ");

  return (
    <>
      <SourceCard
        name={t("integrationsMcpName")}
        meta={t("integrationsMcpMeta")}
        tint={TINT}
        tintDark={TINT_DARK}
        glyph={GlobeIcon}
        status={counts || undefined}
        help={<HelpButton topic="claude-mcp-setup" className="ml-1" />}
        error={error}
        disabledNote={profile != null && profile.mcpAccessEnabled === false ? t("integrationsMcpDisabled") : null}
        actions={
          <button type="button" onClick={() => setDialog(true)} className={cardBtn}>
            {t("integrationsNewToken")}
          </button>
        }
      >
        <div className="flex items-center gap-2">
          <code className="min-w-0 flex-1 truncate rounded bg-gray-100 px-2 py-1 text-xs dark:bg-gray-800 dark:text-gray-200">
            {mcpUrl}
          </code>
          <button
            type="button"
            onClick={() => void navigator.clipboard?.writeText(mcpUrl)}
            className={cardBtn}
          >
            {t("mcpCopyUrl")}
          </button>
        </div>

        <ul className="mt-2">
          {tokens?.map((tk) => (
            <li key={tk.id} className="flex items-center gap-2 border-b border-gray-100 py-1.5 text-xs last:border-b-0 dark:border-gray-800">
              <span className="min-w-0 flex-1 truncate text-[13px] text-gray-800 dark:text-gray-200">
                {tk.name} <code className="text-gray-500 dark:text-gray-400">{tk.prefix}…</code>
                <span className="text-gray-400 dark:text-gray-500">
                  {" · "}
                  {tk.lastUsedAt ? t("mcpLastUsed") : t("mcpNeverUsed")}
                </span>
              </span>
              {/* "Revoke" reads the same on the API card below, so the accessible name says which token. */}
              <button
                type="button"
                onClick={() => revoke(tk.id)}
                aria-label={t("integrationsRevokeAria", { name: tk.name })}
                className="shrink-0 text-red-600 hover:underline dark:text-red-400"
              >
                {t("mcpRevoke")}
              </button>
            </li>
          ))}
          {tokens?.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("mcpNoTokens")}</li>}
        </ul>

        {/* Apps that signed in through Diariz's OAuth flow (the claude.ai connector) rather than pasting a
            token. Different mechanism, same access, so it belongs on the same card. */}
        <div className="mt-3">
          <span className="block text-[11px] font-medium text-gray-500 dark:text-gray-400">
            {t("integrationsConnectedApps")}
          </span>
          <p className="text-[11px] text-gray-400 dark:text-gray-500">{t("integrationsConnectedAppsHint")}</p>
          <ul className="mt-1">
            {connections?.map((c) => (
              <li key={c.id} className="flex items-center gap-2 border-b border-gray-100 py-1.5 text-xs last:border-b-0 dark:border-gray-800">
                <span className="min-w-0 flex-1 truncate text-[13px] text-gray-800 dark:text-gray-200">
                  {c.clientName}
                  {c.connectedAt && (
                    <span className="text-gray-400 dark:text-gray-500">
                      {" · "}
                      {t("mcpConnectedOn", { date: new Date(c.connectedAt).toLocaleDateString() })}
                    </span>
                  )}
                </span>
                <button
                  type="button"
                  onClick={() => disconnect(c.id)}
                  aria-label={t("integrationsDisconnectAria", { name: c.clientName })}
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
      </SourceCard>

      {dialog && (
        <TokenDialog
          title={t("integrationsNewTokenTitle")}
          namePlaceholder={t("mcpTokenNamePlaceholder")}
          onceLabel={t("mcpTokenOnce")}
          snippetLabel={t("mcpShowConfig")}
          snippetFor={configFor}
          snippetNote={t("mcpDesktopNote")}
          busy={busy}
          error={dialogError}
          token={created}
          onGenerate={(name) => void generate(name)}
          onClose={() => {
            setDialog(false);
            setCreated(null);
            setDialogError(null);
          }}
        />
      )}
    </>
  );
}
