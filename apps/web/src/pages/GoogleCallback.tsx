import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../auth";
import { CLEAR_HANDOFF_COOKIE, parseHandoffToken } from "../lib/googleHandoff";

/// Landing route after Google sign-in. The API redirects here (`/auth/google/callback`) after placing the
/// JWT in a short-lived, same-origin cookie — proxy-safe, unlike a URL fragment. We adopt it, clear the
/// cookie, and go to the app. (Failures are redirected by the API to `/login?googleError=…` instead.)
export default function GoogleCallback() {
  const { setSession } = useAuth();
  const navigate = useNavigate();

  useEffect(() => {
    const token = parseHandoffToken(document.cookie);
    document.cookie = CLEAR_HANDOFF_COOKIE; // one-time — expire it immediately
    if (token) {
      setSession(token);
      navigate("/", { replace: true });
    } else {
      navigate("/login?googleError=failed", { replace: true });
    }
  }, [setSession, navigate]);

  return null;
}
