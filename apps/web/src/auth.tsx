import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "./lib/api";
import { emailFromToken, fullNameFromToken, pictureFromToken, rolesFromToken, isAdminFromToken, isPlatformAdminFromToken } from "./lib/jwt";
import { refreshDelayMs } from "./lib/tokenRefresh";
import { initialsFromName, initialsFromEmail } from "./lib/initials";

interface AuthState {
  isAuthed: boolean;
  email: string | null;
  fullName: string | null;
  roles: string[];
  isAdmin: boolean;
  isPlatformAdmin: boolean;
  initials: string;
  /// Profile picture URL from a linked Google account, or null (then the avatar shows initials).
  pictureUrl: string | null;
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
  const pictureUrl = useMemo(() => pictureFromToken(token), [token]);

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

  // Silent sliding-session refresh: re-issue the token shortly before it expires (and when the tab
  // regains focus, in case timers were throttled). This keeps long sessions — e.g. a 45-minute recording
  // left untouched — alive, so Stop never lands on a 401. A failed refresh is ignored; the next real
  // request handles auth normally.
  useEffect(() => {
    if (!token) return;
    let cancelled = false;
    let timer: number | undefined;

    async function doRefresh() {
      try {
        const res = await api.refresh();
        if (!cancelled) setSession(res.accessToken); // updates token → effect reschedules
      } catch {
        // ignore — leave the current token; a later request will surface any auth failure
      }
    }

    function schedule() {
      const delay = refreshDelayMs(getToken(), Date.now());
      if (delay == null) return;
      timer = window.setTimeout(doRefresh, delay);
    }

    function onFocus() {
      const delay = refreshDelayMs(getToken(), Date.now());
      if (delay === 0) void doRefresh(); // within the skew window — refresh now
    }

    schedule();
    window.addEventListener("focus", onFocus);
    return () => {
      cancelled = true;
      if (timer) window.clearTimeout(timer);
      window.removeEventListener("focus", onFocus);
    };
  }, [token]);

  return (
    <AuthContext.Provider
      value={{ isAuthed: Boolean(token), email, fullName, roles, isAdmin, isPlatformAdmin, initials, pictureUrl, login, setSession, logout }}
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
