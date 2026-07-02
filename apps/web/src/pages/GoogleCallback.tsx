import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth";
import { api } from "../lib/api";

/// Landing route after Google sign-in. The API left the access token in a one-time HttpOnly cookie and
/// redirected here; we swap it for the token via `POST /api/auth/google/exchange` (a JSON body — robust
/// against reverse proxies that strip URL fragments or force HttpOnly on cookies), then go to the app.
/// (Failures are redirected by the API to `/login?googleError=…` before reaching this page.)
export default function GoogleCallback() {
  const { setSession } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    let active = true;
    void (async () => {
      const token = await api.exchangeGoogleToken();
      if (!active) return;
      if (token) {
        setSession(token);
        navigate("/", { replace: true });
      } else {
        navigate("/login?googleError=failed", { replace: true });
      }
    })();
    return () => {
      active = false;
    };
  }, [setSession, navigate]);

  return null;
}
