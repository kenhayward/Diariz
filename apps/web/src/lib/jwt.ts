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

/// The signed-in user's id (the JWT `sub` claim), used to scope a locally-stashed unsaved recording so it
/// is only offered back to the same user on this browser.
export function userIdFromToken(token: string | null | undefined): string | null {
  const sub = decodeJwtPayload(token)?.["sub"];
  return typeof sub === "string" ? sub : null;
}

export function fullNameFromToken(token: string | null | undefined): string | null {
  const claim = decodeJwtPayload(token)?.["name"];
  return typeof claim === "string" && claim.trim() ? claim : null;
}

// The role claim may serialize as "role" (compact) or the .NET schema URI, and as a string or array.
const ROLE_KEYS = ["role", "roles", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];

export function rolesFromToken(token: string | null | undefined): string[] {
  const payload = decodeJwtPayload(token);
  if (!payload) return [];
  for (const key of ROLE_KEYS) {
    const claim = payload[key];
    if (Array.isArray(claim)) return claim.filter((r): r is string => typeof r === "string");
    if (typeof claim === "string") return [claim];
  }
  return [];
}

export function isAdminFromToken(token: string | null | undefined): boolean {
  const roles = rolesFromToken(token);
  return roles.includes("Administrator") || roles.includes("PlatformAdministrator");
}

export function isPlatformAdminFromToken(token: string | null | undefined): boolean {
  return rolesFromToken(token).includes("PlatformAdministrator");
}
