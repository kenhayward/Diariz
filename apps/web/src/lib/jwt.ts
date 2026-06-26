/// Decode a JWT payload for DISPLAY ONLY. This does NOT verify the signature — never use it for
/// any security decision; server-side validation remains the source of truth.
export function decodeJwtPayload(token: string | null | undefined): Record<string, unknown> | null {
  if (!token) return null;
  try {
    const payload = token.split(".")[1];
    if (!payload) return null;
    const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"));
    return JSON.parse(json);
  } catch {
    return null;
  }
}

export function emailFromToken(token: string | null | undefined): string | null {
  const claim = decodeJwtPayload(token)?.["email"];
  return typeof claim === "string" ? claim : null;
}
