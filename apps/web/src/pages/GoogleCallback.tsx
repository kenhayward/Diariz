import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth";

/// Landing route after Google sign-in. The API redirects here as `/auth/google/callback#token=<jwt>`,
/// keeping the token in the URL fragment (never sent to the server / logs). We adopt it and go to the app.
/// (Failures are redirected by the API to `/login?googleError=…` instead, so they never reach here.)
export default function GoogleCallback() {
  const { setSession } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    const token = new URLSearchParams(window.location.hash.replace(/^#/, "")).get("token");
    // Strip the fragment so the token isn't left in the address bar or browser history.
    window.history.replaceState(null, "", window.location.pathname);
    if (token) {
      setSession(token);
      navigate("/", { replace: true });
    } else {
      navigate("/login?googleError=failed", { replace: true });
    }
  }, [setSession, navigate]);

  return null;
}
