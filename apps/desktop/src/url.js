"use strict";

/// Normalise a user-entered server address into an origin we can load the web app from.
/// Defaults a bare host to https, drops any path/query/hash, and rejects non-http(s) input.
/// Returns the origin (e.g. "https://diariz.example.com") or null when invalid.
function normalizeServerUrl(input) {
  const raw = (input || "").trim();
  if (!raw) return null;
  const hasHttp = /^https?:\/\//i.test(raw);
  // An explicit non-http(s) scheme (ftp://, file://, …) is rejected rather than coerced.
  if (!hasHttp && /^[a-z][a-z0-9+.-]*:\/\//i.test(raw)) return null;
  const withScheme = hasHttp ? raw : `https://${raw}`;
  try {
    const u = new URL(withScheme);
    if (u.protocol !== "http:" && u.protocol !== "https:") return null;
    if (!u.hostname) return null;
    return u.origin;
  } catch {
    return null;
  }
}

module.exports = { normalizeServerUrl };
