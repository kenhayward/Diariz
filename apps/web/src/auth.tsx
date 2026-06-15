import { createContext, useContext, useState, type ReactNode } from "react";
import { api, getToken, setToken } from "./lib/api";

interface AuthState {
  isAuthed: boolean;
  login: (email: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [isAuthed, setIsAuthed] = useState(() => Boolean(getToken()));

  async function login(email: string, password: string) {
    const res = await api.login(email, password);
    setToken(res.accessToken);
    setIsAuthed(true);
  }

  function logout() {
    setToken(null);
    setIsAuthed(false);
  }

  return (
    <AuthContext.Provider value={{ isAuthed, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
