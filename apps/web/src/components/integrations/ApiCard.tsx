import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../../lib/api";
import { CodeIcon } from "../icons";
import HelpButton from "../HelpButton";
import SourceCard, { cardBtn } from "../SourceCard";
import TokenDialog from "./TokenDialog";

const TINT = "#6b7280";
const TINT_DARK = "#cbd5e1";

/// API access: the REST base URL and the personal tokens that call it as you.
///
/// The read-only flag and the expiry date used to sit under the Generate button as though they were
/// settings you could revisit. They are neither - both are fixed when the token is minted - so they moved
/// into the generate dialog, and the token list finally shows what was chosen: which tokens are read-only
/// and when each expires. That was already on the wire and simply never rendered.
export default function ApiCard() {
  const { t, i18n } = useTranslation("account");
  const qc = useQueryClient();
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const enabled = profile?.apiAccessEnabled === true;

  const { data: tokens } = useQuery({ queryKey: ["api-tokens"], queryFn: api.listApiTokens, enabled });

  const [dialog, setDialog] = useState(false);
  const [created, setCreated] = useState<string | null>(null);
  const [readOnly, setReadOnly] = useState(false);
  const [expiresAt, setExpiresAt] = useState(""); // yyyy-mm-dd or empty
  const [busy, setBusy] = useState(false);
  const [dialogError, setDialogError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const baseUrl = `${window.location.origin}/api`;
  const exampleFor = (token: string) => `curl -H "Authorization: Bearer ${token}" ${baseUrl}/recordings`;

  async function generate(name: string) {
    setDialogError(null);
    setBusy(true);
    try {
      const tok = await api.createApiToken(name || t("apiDefaultName"), {
        readOnly,
        expiresAt: expiresAt ? new Date(expiresAt + "T23:59:59Z").toISOString() : null,
      });
      setCreated(tok.token);
      qc.invalidateQueries({ queryKey: ["api-tokens"] });
    } catch (e) {
      setDialogError(apiErrorMessage(e, t("apiGenerateError")));
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

  function closeDialog() {
    setDialog(false);
    setCreated(null);
    setDialogError(null);
    // Creation-time choices reset with the dialog: leaving "read-only" ticked from last time would mint a
    // token the user did not ask for, and they cannot tell afterwards without reading the list.
    setReadOnly(false);
    setExpiresAt("");
  }

  const date = (iso: string) => new Date(iso).toLocaleDateString(i18n.language);

  return (
    <>
      <SourceCard
        name={t("integrationsApiName")}
        meta={t("integrationsApiMeta")}
        tint={TINT}
        tintDark={TINT_DARK}
        glyph={CodeIcon}
        status={tokens?.length ? t("integrationsTokenCount", { count: tokens.length }) : undefined}
        help={<HelpButton topic="api-overview" className="ml-1" />}
        error={error}
        disabledNote={enabled ? null : t("integrationsApiDisabled")}
        actions={
          <>
            <a href="/developers/api" target="_blank" rel="noopener noreferrer" className={cardBtn}>
              {t("apiViewReference")}
            </a>
            <button type="button" onClick={() => setDialog(true)} className={cardBtn}>
              {t("integrationsNewToken")}
            </button>
          </>
        }
      >
        <div className="flex items-center gap-2">
          <code className="min-w-0 flex-1 truncate rounded bg-gray-100 px-2 py-1 text-xs dark:bg-gray-800 dark:text-gray-200">
            {baseUrl}
          </code>
          <button
            type="button"
            onClick={() => void navigator.clipboard?.writeText(baseUrl)}
            className={cardBtn}
          >
            {t("apiCopyUrl")}
          </button>
        </div>

        <ul className="mt-2">
          {tokens?.map((tk) => (
            <li key={tk.id} className="flex items-center gap-2 border-b border-gray-100 py-1.5 text-xs last:border-b-0 dark:border-gray-800">
              <span className="min-w-0 flex-1 truncate text-[13px] text-gray-800 dark:text-gray-200">
                {tk.name} <code className="text-gray-500 dark:text-gray-400">{tk.prefix}…</code>
                {tk.scope === "ReadOnly" && (
                  <span className="ml-1 rounded bg-gray-100 px-1 text-[10px] text-gray-500 dark:bg-slate-400/[0.16] dark:text-slate-300">
                    {t("apiReadOnlyChip")}
                  </span>
                )}
                <span className="text-gray-400 dark:text-gray-500">
                  {" · "}
                  {tk.lastUsedAt ? t("apiLastUsed") : t("apiNeverUsed")}
                  {tk.expiresAt && ` · ${t("integrationsExpiresOn", { date: date(tk.expiresAt) })}`}
                </span>
              </span>
              <button
                type="button"
                onClick={() => revoke(tk.id)}
                aria-label={t("integrationsRevokeAria", { name: tk.name })}
                className="shrink-0 text-red-600 hover:underline dark:text-red-400"
              >
                {t("apiRevoke")}
              </button>
            </li>
          ))}
          {tokens?.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("apiNoTokens")}</li>}
        </ul>
      </SourceCard>

      {dialog && (
        <TokenDialog
          title={t("integrationsNewTokenTitle")}
          namePlaceholder={t("apiTokenNamePlaceholder")}
          onceLabel={t("apiTokenOnce")}
          snippetLabel={t("apiShowExample")}
          snippetFor={exampleFor}
          busy={busy}
          error={dialogError}
          token={created}
          onGenerate={(name) => void generate(name)}
          onClose={closeDialog}
          options={
            <>
              <label className="mt-2 flex items-center gap-2 text-xs dark:text-gray-200">
                <input type="checkbox" checked={readOnly} onChange={(e) => setReadOnly(e.target.checked)} />
                {t("apiReadOnly")}
              </label>
              <label className="mt-1 flex items-center gap-2 text-xs dark:text-gray-200">
                {t("apiExpiresOn")}
                <input
                  type="date"
                  value={expiresAt}
                  onChange={(e) => setExpiresAt(e.target.value)}
                  className="rounded border px-2 py-1 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
              </label>
            </>
          }
        />
      )}
    </>
  );
}
