import { createContext, useContext, useMemo, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "./lib/api";
import { emailFromToken } from "./lib/jwt";
import { initialsFromEmail } from "./lib/initials";

interface AuthState {
  isAuthed: boolean;
  email: string | null;
  initials: string;
  login: (email: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setTokenState] = useState<string | null>(() => getToken());

  const email = useMemo(() => emailFromToken(token), [token]);
  const initials = useMemo(() => initialsFromEmail(email), [email]);

  async function login(emailArg: string, password: string) {
    const res = await api.login(emailArg, password);
    setToken(res.accessToken);
    setTokenState(res.accessToken);
  }

  function logout() {
    setToken(null);
    setTokenState(null);
  }

  return (
    <AuthContext.Provider value={{ isAuthed: Boolean(token), email, initials, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
