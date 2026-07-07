import { useEffect, useState } from "react";
import axios from "axios";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import { isElectron } from "../lib/audioSource";
import AuthShell from "../components/AuthShell";
import GoogleSignInButton from "../components/GoogleSignInButton";

export default function Login() {
  const { t } = useTranslation("auth");
  const { login } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Google sign-in is offered when the server has it configured. In the Electron shell it runs through
  // the system browser (via IPC) because Google blocks OAuth in embedded webviews; on the web it is a
  // normal full-page redirect.
  const [googleEnabled, setGoogleEnabled] = useState(false);
  useEffect(() => {
    let active = true;
    api.getAuthProviders().then((p) => active && setGoogleEnabled(p.google)).catch(() => {});
    return () => {
      active = false;
    };
  }, []);

  // The Google flow redirects failures back here as ?googleError=<reason>.
  const googleError = params.get("googleError");
  const googleErrorMsg = googleError
    ? t(
        googleError === "pending"
          ? "googleAwaitingApproval"
          : googleError === "disabled"
            ? "googleAccountDisabled"
            : "googleSignInFailed",
      )
    : null;

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(email, password);
      // Honour an internal ?returnTo= (e.g. the OAuth consent screen sends the user here first). Only
      // same-app absolute paths are allowed, never an external URL.
      const returnTo = params.get("returnTo");
      const dest = returnTo && returnTo.startsWith("/") && !returnTo.startsWith("//") ? returnTo : "/";
      navigate(dest);
    } catch (err) {
      // 401 from the API has no body and genuinely means bad credentials;
      // anything else (500, network) shows the real reason.
      setError(
        axios.isAxiosError(err) && err.response?.status === 401
          ? t("invalidCredentials")
          : apiErrorMessage(err, t("invalidCredentials")),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthShell>
      <form
        onSubmit={onSubmit}
        className="w-full max-w-sm space-y-4 rounded-lg border bg-white p-6 shadow-xl dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center gap-2">
          <img src="/logo.png" alt="" className="h-8 w-auto" />
          <h1 className="text-lg font-semibold dark:text-gray-100">{t("signInTitle")}</h1>
        </div>
        <input
          type="email"
          placeholder={t("email")}
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          className="w-full rounded border px-3 py-2 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          required
        />
        <input
          type="password"
          placeholder={t("password")}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          className="w-full rounded border px-3 py-2 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          required
        />
        {(error || googleErrorMsg) && (
          <p className="text-sm text-red-600 dark:text-red-400">{error ?? googleErrorMsg}</p>
        )}
        <button
          type="submit"
          disabled={busy}
          className="w-full rounded bg-gray-900 py-2 text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {busy ? t("signingIn") : t("signIn")}
        </button>
        {googleEnabled && (
          <>
            <div className="flex items-center gap-2 text-xs text-gray-400">
              <span className="h-px flex-grow bg-gray-200 dark:bg-gray-700" />
              {t("or")}
              <span className="h-px flex-grow bg-gray-200 dark:bg-gray-700" />
            </div>
            <GoogleSignInButton
              label={t("signInWithGoogle")}
              onClick={isElectron ? () => window.diariz?.startGoogleSignIn?.() : undefined}
            />
          </>
        )}
        <p className="text-center text-sm text-gray-500 dark:text-gray-400">
          {t("needAccount")}{" "}
          <Link to="/request-access" className="text-blue-600 hover:underline dark:text-blue-400">
            {t("requestAccess")}
          </Link>
        </p>
      </form>
    </AuthShell>
  );
}
