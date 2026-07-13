import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, getToken, setToken } from "./lib/api";
import type { Permissions } from "./lib/types";
import { emailFromToken, fullNameFromToken, pictureFromToken } from "./lib/jwt";
import { refreshDelayMs } from "./lib/tokenRefresh";
import { initialsFromName, initialsFromEmail } from "./lib/initials";

/// No authority until the server says otherwise: the profile is fetched, not decoded from the token.
const NO_PERMISSIONS: Permissions = { manageRooms: false, manageUsers: false, managePlatform: false, manageFormulas: false };

interface AuthState {
  isAuthed: boolean;
  email: string | null;
  fullName: string | null;
  /// The caller's platform permissions, from GET /api/user/profile. Never inferred from the JWT: a token
  /// claim would keep granting authority until it expired, long after the user left the group.
  permissions: Permissions;
  isAdmin: boolean;
  isPlatformAdmin: boolean;
  canManageFormulas: boolean;
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
  const qc = useQueryClient();

  const email = useMemo(() => emailFromToken(token), [token]);
  const fullName = useMemo(() => fullNameFromToken(token), [token]);
  const initials = useMemo(() => (fullName ? initialsFromName(fullName) : initialsFromEmail(email)), [fullName, email]);
  const pictureUrl = useMemo(() => pictureFromToken(token), [token]);

  // Authority lives on the profile, so a group change takes effect on the next fetch rather than at token
  // expiry. Until it arrives (or if it fails) the user holds nothing - the UI fails closed.
  const { data: profile } = useQuery({
    queryKey: ["user-profile"],
    queryFn: api.getProfile,
    enabled: Boolean(token),
  });
  const permissions = profile?.permissions ?? NO_PERMISSIONS;
  const isAdmin = permissions.manageUsers;
  const isPlatformAdmin = permissions.managePlatform;
  const canManageFormulas = permissions.manageFormulas;

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
    // Drop every cached query, the profile above all: otherwise the next user to sign in on this browser
    // sees the previous user's permissions (and their recordings) until each refetch lands.
    qc.clear();
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

  // Desktop shell: after a diariz:// Google sign-in, the shell pushes the access token here. Adopt it
  // through the same path as a normal login (persist + schedule refresh). No-op in a plain browser.
  useEffect(() => {
    const unsub = window.diariz?.onAuthToken?.((incoming) => setSession(incoming));
    return () => unsub?.();
  }, []);

  return (
    <AuthContext.Provider
      value={{
        isAuthed: Boolean(token),
        email,
        fullName,
        permissions,
        isAdmin,
        isPlatformAdmin,
        canManageFormulas,
        initials,
        pictureUrl,
        login,
        setSession,
        logout,
      }}
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
