import { useEffect, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { useAuth } from "../auth";
import { api, apiErrorMessage } from "../lib/api";
import { fetchLanguages } from "../lib/languages";
import { setStoredLanguage } from "../lib/language";
import type { Language } from "../lib/types";
import AuthShell from "../components/AuthShell";

/// Public page reached from the one-time setup link. Validates the link, then collects a full name
/// and password to activate the account and sign the user in.
export default function Setup() {
  const [params] = useSearchParams();
  const email = params.get("email") ?? "";
  const token = params.get("token") ?? "";
  const navigate = useNavigate();
  const { setSession } = useAuth();

  const [checking, setChecking] = useState(true);
  const [valid, setValid] = useState(false);
  const [fullName, setFullName] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [language, setLanguage] = useState(""); // chosen UI language (blank = follow browser)
  const [languages, setLanguages] = useState<Language[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    api
      .validateSetup(email, token)
      .then((r) => {
        if (!active) return;
        setValid(r.valid);
        if (r.fullName) setFullName(r.fullName); // pre-fill the name captured at request/add time
      })
      .catch(() => active && setValid(false))
      .finally(() => active && setChecking(false));
    fetchLanguages()
      .then((l) => active && setLanguages(l))
      .catch(() => {}); // a missing list just hides the selector — never blocks setup
    return () => {
      active = false;
    };
  }, [email, token]);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (password !== confirm) {
      setError("Passwords don't match.");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const res = await api.setup({ email, token, fullName: fullName.trim(), password });
      setSession(res.accessToken);
      setStoredLanguage(language || null); // cache the choice locally for an immediate UI effect
      if (language) {
        // Persist the language to the new account (best-effort — never block sign-in if it fails).
        try {
          const updated = await api.updateProfile({
            fullName: fullName.trim() || null,
            nativeLanguage: language,
            uiLanguage: language,
          });
          setSession(updated.accessToken);
        } catch {
          /* ignore */
        }
      }
      navigate("/");
    } catch (err) {
      setError(apiErrorMessage(err, "Could not complete setup."));
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthShell>
      <div className="w-full max-w-sm space-y-4 rounded-lg border bg-white p-6 shadow-xl dark:border-gray-700 dark:bg-gray-900">
        <div className="flex items-center gap-2">
          <img src="/logo.png" alt="" className="h-8 w-auto" />
          <h1 className="text-lg font-semibold dark:text-gray-100">Set up your account</h1>
        </div>

        {checking ? (
          <p className="text-sm text-gray-500 dark:text-gray-400">Checking your link…</p>
        ) : !valid ? (
          <div className="space-y-3">
            <p className="text-sm text-red-600 dark:text-red-400">
              This setup link is invalid or has expired. Please request access again.
            </p>
            <Link to="/request-access" className="text-sm text-blue-600 hover:underline dark:text-blue-400">
              Request access
            </Link>
          </div>
        ) : (
          <form onSubmit={onSubmit} className="space-y-3" autoComplete="off">
            <p className="text-xs text-gray-500 dark:text-gray-400">Setting up {email}</p>
            <input
              type="text"
              placeholder="Full name"
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
              className="w-full rounded border px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              required
            />
            <input
              type="password"
              placeholder="Password"
              autoComplete="new-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full rounded border px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              required
            />
            <input
              type="password"
              placeholder="Confirm password"
              autoComplete="new-password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              className="w-full rounded border px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
              required
            />
            <p className="text-xs text-gray-400 dark:text-gray-500">
              At least 8 characters with an uppercase letter, a lowercase letter, a number, and a symbol.
            </p>
            {languages.length > 0 && (
              <label className="block text-sm">
                <span className="mb-1 block text-gray-600 dark:text-gray-300">Language</span>
                <select
                  value={language}
                  onChange={(e) => setLanguage(e.target.value)}
                  aria-label="Language"
                  className="w-full rounded border px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                >
                  <option value="">Follow my browser</option>
                  {languages.map((l) => (
                    <option key={l.code} value={l.code}>
                      {l.englishName} ({l.nativeName})
                    </option>
                  ))}
                </select>
              </label>
            )}
            {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
            <button
              type="submit"
              disabled={busy}
              className="w-full rounded bg-gray-900 py-2 text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
            >
              {busy ? "Setting up…" : "Create account"}
            </button>
          </form>
        )}
      </div>
    </AuthShell>
  );
}
