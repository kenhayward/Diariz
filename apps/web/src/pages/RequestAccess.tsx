import { useState } from "react";
import { Link } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import AuthShell from "../components/AuthShell";

/// Public page: anyone can request access. The response is intentionally neutral (it never reveals
/// whether the email already has an account); an administrator reviews and grants the request.
export default function RequestAccess() {
  const { t } = useTranslation("auth");
  const [email, setEmail] = useState("");
  const [fullName, setFullName] = useState("");
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.requestAccess(email.trim(), fullName.trim() || undefined);
      setDone(true);
    } catch (err) {
      setError(apiErrorMessage(err, t("requestFailed")));
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthShell>
      <div className="w-full max-w-sm space-y-4 rounded-lg border bg-white p-6 shadow-xl dark:border-gray-700 dark:bg-gray-900">
        <div className="flex items-center gap-2">
          <img src="/logo.png" alt="" className="h-8 w-auto" />
          <h1 className="text-lg font-semibold dark:text-gray-100">{t("requestTitle")}</h1>
        </div>

        {done ? (
          <div className="space-y-3">
            <p className="text-sm text-gray-600 dark:text-gray-300">{t("requestSubmitted")}</p>
            <Link to="/login" className="text-sm text-blue-600 hover:underline dark:text-blue-400">
              {t("backToSignIn")}
            </Link>
          </div>
        ) : (
          <form onSubmit={onSubmit} className="space-y-4">
            <input
              type="text"
              placeholder={t("yourName")}
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              aria-label={t("yourName")}
              className="w-full rounded border px-3 py-2 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
            <input
              type="email"
              placeholder={t("yourEmail")}
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="w-full rounded border px-3 py-2 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              required
            />
            {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
            <button
              type="submit"
              disabled={busy || !email.trim()}
              className="w-full rounded bg-gray-900 py-2 text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
            >
              {busy ? t("submitting") : t("requestAccess")}
            </button>
            <Link to="/login" className="block text-center text-sm text-blue-600 hover:underline dark:text-blue-400">
              {t("backToSignIn")}
            </Link>
          </form>
        )}
      </div>
    </AuthShell>
  );
}
