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
  for (const arg of argv || []) {
    if (typeof arg !== "string" || !arg.startsWith(DEEP_LINK_PREFIX)) continue;
    const q = arg.indexOf("?");
    if (q === -1) continue;
    const code = new URLSearchParams(arg.slice(q + 1)).get("code");
    if (code) return code;
  }
  return null;
}

module.exports = { buildStartUrl, codeFromArgv };
