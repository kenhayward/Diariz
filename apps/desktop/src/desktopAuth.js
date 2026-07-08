"use strict";

// Pure helpers for the desktop Google sign-in deep-link flow. Kept free of Electron so they can be
// unit-tested with `node --test` (same pattern as recorderState.js / updateState.js). Crypto
// (verifier/challenge generation) lives in main.js; only the string/URL shaping is here.

const DEEP_LINK_PREFIX = "diariz://auth/callback";

/// Build the API's Google-start URL for a desktop flow: carries the S256 PKCE challenge so the
/// server marks the flow as desktop and hands back a code instead of the SPA cookie.
function buildStartUrl(serverOrigin, challenge) {
  const base = String(serverOrigin || "").replace(/\/+$/, "");
  return `${base}/api/auth/google/start?desktopChallenge=${encodeURIComponent(challenge)}`;
}

/// Extract the one-time code from a diariz://auth/callback?code=… deep link found anywhere in an
/// argv array (Windows delivers the URL as a process argument). Returns null if absent/malformed.
function codeFromArgv(argv) {
  // Require the exact callback path followed by a query, so a look-alike host/path
  // (e.g. diariz://auth/callback-evil?code=x) is not accepted.
  const prefix = DEEP_LINK_PREFIX + "?";
  for (const arg of argv || []) {
    if (typeof arg !== "string" || !arg.startsWith(prefix)) continue;
    const code = new URLSearchParams(arg.slice(prefix.length)).get("code");
    if (code) return code;
  }
  return null;
}

/// Native-notification copy for a failed desktop sign-in, by reason. Pure (returns { title, body }) so
/// it's unit-testable; main.js shows it via Electron's Notification. Mirrors updateState's
/// notificationForUpdate. Reasons: "network" (couldn't reach the server), "expired" (the sign-in state
/// was lost - e.g. the app restarted mid-flow, or a stale/duplicate deep link), else "rejected"/generic.
function notificationForAuthError(reason) {
  const body =
    reason === "network"
      ? "Couldn't reach the server to finish signing in. Check your connection and try again."
      : reason === "expired"
        ? "Sign-in was interrupted. Please sign in again."
        : "Google sign-in didn't complete. Please try again.";
  return { title: "Diariz", body };
}

module.exports = { buildStartUrl, codeFromArgv, notificationForAuthError };
