/// Google sign-in success handoff. The API delivers the freshly-minted JWT in a short-lived, same-origin
/// cookie (robust across reverse proxies that strip URL fragments from a redirect's Location); the callback
/// page reads it, adopts the session, then clears the cookie.
export const HANDOFF_COOKIE = "diariz_auth";

/// Extract the handoff token from a `document.cookie` string, or null if it isn't present.
export function parseHandoffToken(cookie: string): string | null {
  const m = cookie.match(new RegExp(`(?:^|;\\s*)${HANDOFF_COOKIE}=([^;]+)`));
  return m ? decodeURIComponent(m[1]) : null;
}

/// The `document.cookie` assignment that expires the handoff cookie (must match its Path).
export const CLEAR_HANDOFF_COOKIE = `${HANDOFF_COOKIE}=; Path=/auth/google/callback; Max-Age=0`;
