// Client-side gate for the "Upload" button. This is UX only — the server independently validates the
// actual file bytes (magic-byte sniff) and enforces size + the per-user quota.

/// Extensions offered in the file picker / accepted by the pre-check. M4A/AAC are listed here, but the
/// server may reject them if AAC is disabled there.
export const ACCEPTED_AUDIO_EXTENSIONS = ["wav", "mp3", "flac", "ogg", "opus", "webm", "m4a", "aac"] as const;

/// The `accept` attribute for the hidden <input type="file">.
export const AUDIO_ACCEPT_ATTR = ACCEPTED_AUDIO_EXTENSIONS.map((e) => `.${e}`).join(",") + ",audio/*";

/// Mirrors the server's default 500 MB cap so the user gets instant feedback (server is authoritative).
export const MAX_UPLOAD_BYTES = 500 * 1024 * 1024;

/// Lower-cased extension without the dot, or "" if none.
export function fileExtension(name: string): string {
  const dot = name.lastIndexOf(".");
  return dot >= 0 ? name.slice(dot + 1).toLowerCase() : "";
}

export function isAcceptedAudioFilename(name: string): boolean {
  return (ACCEPTED_AUDIO_EXTENSIONS as readonly string[]).includes(fileExtension(name));
}

/// A default title from a filename (drop the extension), or a sensible fallback.
export function titleFromFilename(name: string): string {
  const dot = name.lastIndexOf(".");
  const base = (dot >= 0 ? name.slice(0, dot) : name).trim();
  return base || "Uploaded audio";
}

/// Returns an error message if the file should be rejected before upload, or null if it's OK to send.
export function precheckUpload(file: { name: string; size: number }, maxBytes = MAX_UPLOAD_BYTES): string | null {
  if (!isAcceptedAudioFilename(file.name))
    return "Unsupported file type. Use WAV, MP3, FLAC, Ogg/Opus, WebM, or M4A.";
  if (file.size === 0) return "That file is empty.";
  if (maxBytes > 0 && file.size > maxBytes)
    return `File too large. The maximum is ${Math.round(maxBytes / (1024 * 1024))} MB.`;
  return null;
}
