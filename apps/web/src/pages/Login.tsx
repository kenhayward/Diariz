import { useState } from "react";
import axios from "axios";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth";
import { apiErrorMessage } from "../lib/api";

export default function Login() {
  const { login } = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(email, password);
      navigate("/");
    } catch (err) {
      // 401 from the API has no body and genuinely means bad credentials;
      // anything else (500, network) shows the real reason.
      setError(
        axios.isAxiosError(err) && err.response?.status === 401
          ? "Invalid email or password."
          : apiErrorMessage(err, "Invalid email or password."),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-gray-50 p-4 dark:bg-gray-950">
      <form
        onSubmit={onSubmit}
        className="w-full max-w-sm space-y-4 rounded-lg border bg-white p-6 dark:border-gray-700 dark:bg-gray-900"
      >
        <div className="flex items-center gap-2">
          <img src="/logo.png" alt="" className="h-8 w-auto" />
          <h1 className="text-lg font-semibold dark:text-gray-100">Sign in to Diariz</h1>
        </div>
        <input
          type="email"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          className="w-full rounded border px-3 py-2 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          required
        />
        <input
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          className="w-full rounded border px-3 py-2 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          required
        />
        {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}
        <button
          type="submit"
          disabled={busy}
          className="w-full rounded bg-gray-900 py-2 text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
        >
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </div>
  );
}
