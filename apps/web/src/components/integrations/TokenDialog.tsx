import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

/// The generate-a-token dialog, shared by the MCP and API cards. Two states in one: the form, then - once
/// the server has answered - the one-time secret, which the dialog will not close behind.
///
/// Generating moved off the card and into here because a token's creation-time choices (a name, and for
/// the API a read-only flag and an expiry) were sitting on the card as though they were settings. They are
/// not: they are decided once, at the moment the token is minted, and they cannot be changed afterwards.

/// The amber treatment for a secret shown exactly once. The loudest thing on the page, deliberately - it
/// is the only moment the value exists anywhere the user can copy it from.
const ONCE_BOX = "rounded border border-amber-300 bg-amber-50 p-2 dark:border-amber-700/60 dark:bg-amber-900/20";

export default function TokenDialog({
  title,
  namePlaceholder,
  onceLabel,
  snippetLabel,
  snippetFor,
  snippetNote,
  options,
  busy,
  error,
  token,
  onGenerate,
  onClose,
}: {
  title: string;
  namePlaceholder: string;
  /// "Copy this now - you will not see it again."
  onceLabel: string;
  /// The disclosure label for the ready-to-paste snippet ("Show Claude Desktop config" / "Show example request").
  snippetLabel: string;
  /// Builds the snippet from the freshly minted secret.
  snippetFor: (token: string) => string;
  snippetNote?: string;
  /// Creation-time choices this kind of token has. The MCP dialog has none.
  options?: ReactNode;
  busy: boolean;
  error: string | null;
  /// Set once the server has answered: the dialog switches from form to secret.
  token: string | null;
  onGenerate: (name: string) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation("account");
  const [name, setName] = useState("");
  const copy = (text: string) => void navigator.clipboard?.writeText(text);

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="w-full max-w-md rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="mb-3 flex items-center justify-between gap-2">
          <h3 className="text-sm font-semibold dark:text-gray-100">{title}</h3>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("common:close")}
            className="rounded px-1 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
          >
            ✕
          </button>
        </div>

        {token ? (
          <div className={ONCE_BOX}>
            <p className="text-xs font-medium text-amber-800 dark:text-amber-300">{onceLabel}</p>
            <div className="mt-1 flex items-center gap-2">
              <code className="flex-1 break-all rounded bg-white px-2 py-1 text-xs dark:bg-gray-900 dark:text-gray-100">
                {token}
              </code>
              <button type="button" onClick={() => copy(token)} className={dialogBtn}>
                {t("apiCopyToken")}
              </button>
            </div>
            <details className="mt-2">
              <summary className="cursor-pointer text-xs text-gray-600 dark:text-gray-300">{snippetLabel}</summary>
              <pre className="mt-1 overflow-x-auto rounded bg-white p-2 text-[11px] leading-snug dark:bg-gray-900 dark:text-gray-200">
                {snippetFor(token)}
              </pre>
              <button type="button" onClick={() => copy(snippetFor(token))} className={`mt-1 ${dialogBtn}`}>
                {t("mcpCopyConfig")}
              </button>
              {snippetNote && (
                <p className="mt-1 text-[11px] leading-snug text-gray-400 dark:text-gray-500">{snippetNote}</p>
              )}
            </details>
          </div>
        ) : (
          <>
            <input
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={namePlaceholder}
              aria-label={namePlaceholder}
              className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
            {options}
            {error && <p className="mt-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
          </>
        )}

        <div className="mt-4 flex justify-end gap-2">
          {token ? (
            <button type="button" onClick={onClose} className={dialogBtn}>
              {t("common:close")}
            </button>
          ) : (
            <>
              <button type="button" onClick={onClose} className={dialogBtn}>
                {t("common:cancel")}
              </button>
              <button
                type="button"
                onClick={() => onGenerate(name.trim())}
                disabled={busy}
                className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
              >
                {t("integrationsGenerate")}
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

const dialogBtn =
  "rounded border px-2 py-1 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";
