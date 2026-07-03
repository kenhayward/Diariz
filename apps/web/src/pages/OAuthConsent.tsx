import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import AuthShell from "../components/AuthShell";

/// The OAuth consent screen for the MCP web connector. The API's /connect/authorize endpoint redirects the
/// browser here (with the original authorize query) when it has no consent decision yet. The signed-in user
/// approves or denies; we record the decision server-side (a short-lived cookie) and then navigate the browser
/// back to /connect/authorize, which completes the OAuth flow (issues the code, or returns access_denied).
export default function OAuthConsent() {
  const { t } = useTranslation("oauth");
  const { isAuthed } = useAuth();
  const navigate = useNavigate();

  // The exact /connect/authorize query the server sent us here with - replayed verbatim to complete the flow.
  const search = window.location.search;
  const clientId = new URLSearchParams(search).get("client_id") ?? "";

  const [clientName, setClientName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Not signed in - send to login, returning to this exact consent URL afterwards.
  useEffect(() => {
    if (!isAuthed) {
      const returnTo = `/oauth/consent${search}`;
      navigate(`/login?returnTo=${encodeURIComponent(returnTo)}`, { replace: true });
    }
  }, [isAuthed, navigate, search]);

  // Fetch the requesting client's display name for the prompt.
  useEffect(() => {
    if (!isAuthed || !clientId) return;
    let active = true;
    api
      .oauthConsentInfo(clientId)
      .then((info) => active && setClientName(info.clientName))
      .catch(() => active && setError(t("unknownClient")));
    return () => {
      active = false;
    };
  }, [isAuthed, clientId, t]);

  async function decide(allow: boolean) {
    setBusy(true);
    setError(null);
    try {
      await api.oauthConsent(clientId, allow);
      // Top-level navigation back to the authorize endpoint: it reads the consent cookie and finishes the
      // flow (redirecting to the client with the code on allow, or an access_denied error on deny).
      window.location.assign(`/connect/authorize${search}`);
    } catch (err) {
      setError(apiErrorMessage(err, t("consentFailed")));
      setBusy(false);
    }
  }

  if (!isAuthed) return null;

  return (
    <AuthShell>
      <div className="w-full max-w-sm space-y-4 rounded-lg border bg-white p-6 shadow-xl dark:border-gray-700 dark:bg-gray-900">
        <div className="flex items-center gap-2">
          <img src="/logo.png" alt="" className="h-8 w-auto" />
          <h1 className="text-lg font-semibold dark:text-gray-100">{t("title")}</h1>
        </div>

        {error ? (
          <p className="text-sm text-red-600 dark:text-red-400">{error}</p>
        ) : !clientId ? (
          <p className="text-sm text-red-600 dark:text-red-400">{t("missingRequest")}</p>
        ) : (
          <>
            <p className="text-sm text-gray-600 dark:text-gray-300">
              {t("intro", { client: clientName ?? t("anApp") })}
            </p>
            <ul className="list-disc space-y-1 pl-5 text-sm text-gray-600 dark:text-gray-300">
              <li>{t("permRead")}</li>
              <li>{t("permEmail")}</li>
            </ul>
            <p className="text-xs text-gray-500 dark:text-gray-400">{t("revokeNote")}</p>
            <div className="flex gap-3 pt-1">
              <button
                onClick={() => decide(true)}
                disabled={busy || !clientName}
                className="flex-1 rounded bg-gray-900 py-2 text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
              >
                {t("allow")}
              </button>
              <button
                onClick={() => decide(false)}
                disabled={busy}
                className="flex-1 rounded border py-2 disabled:opacity-50 dark:border-gray-700 dark:text-gray-100"
              >
                {t("deny")}
              </button>
            </div>
          </>
        )}
      </div>
    </AuthShell>
  );
}
