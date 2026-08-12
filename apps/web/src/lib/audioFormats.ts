// Shared filename/size helpers for uploads. Classification of *what a dropped file is* lives in
// mediaKinds.ts; this is UX only — the server independently validates the actual file bytes
// (magic-byte sniff) and enforces size + the per-user quota.

/// Mirrors the server's default 500 MB cap so the user gets instant feedback (server is authoritative).
export const MAX_UPLOAD_BYTES = 500 * 1024 * 1024;

/// Lower-cased extension without the dot, or "" if none.
export function fileExtension(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot >= 0 ? name.slice(dot + 1).toLowerCase() : "";
}

/// A default title from a filename (drop the extension), or a sensible fallback.
export function titleFromFilename(name: string): string {
  const dot = name.lastIndexOf(".");
  const base = (dot >= 0 ? name.slice(0, dot) : name).trim();
  return base || "Uploaded audio";
}
