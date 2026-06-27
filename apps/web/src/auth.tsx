import { createContext, useContext, useMemo, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "./lib/api";
import { emailFromToken, fullNameFromToken, rolesFromToken, isAdminFromToken, isPlatformAdminFromToken } from "./lib/jwt";
import { initialsFromName, initialsFromEmail } from "./lib/initials";

interface AuthState {
  isAuthed: boolean;
  email: string | null;
  fullName: string | null;
  roles: string[];
  isAdmin: boolean;
  isPlatformAdmin: boolean;
  initials: string;
  login: (email: string, password: string) => Promise<void>;
  /// Adopt an access token directly (e.g. after account setup auto-signs the user in).
  setSession: (token: string) => void;
  logout: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setTokenState] = useState<string | null>(() => getToken());

  const email = useMemo(() => emailFromToken(token), [token]);
  const fullName = useMemo(() => fullNameFromToken(token), [token]);
  const roles = useMemo(() => rolesFromToken(token), [token]);
  const isAdmin = useMemo(() => isAdminFromToken(token), [token]);
  const isPlatformAdmin = useMemo(() => isPlatformAdminFromToken(token), [token]);
  const initials = useMemo(() => (fullName ? initialsFromName(fullName) : initialsFromEmail(email)), [fullName, email]);

  function setSession(accessToken: string) {
    setToken(accessToken);
    setTokenState(accessToken);
  }

  async function login(emailArg: string, password: string) {
    const res = await api.login(emailArg, password);
    setSession(res.accessToken);
  }

  function logout() {
    setToken(null);
    setTokenState(null);
  }

  return (
    <AuthContext.Provider
      value={{ isAuthed: Boolean(token), email, fullName, roles, isAdmin, isPlatformAdmin, initials, login, setSession, logout }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
