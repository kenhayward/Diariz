/// Clipboard helpers for sharing a persistent link to a transcript.

/// The persistent, owner-only deep-link to a transcript (the existing SPA route). Anyone opening it
/// must be signed in as the owner — it's a personal bookmark, not a public share link.
export function transcriptUrl(id: string): string {
  return `${window.location.origin}/recordings/${id}`;
}

/// The persistent deep-link to a folder (section) page. `roomBasePath` (from useRoomBasePath, resolved
/// against the folder's OWN room - not necessarily the room currently showing in the URL) prefixes the link
/// with `/rooms/:id` for a shared-room folder, so a freshly copied link stays room-aware. Older links (and any
/// call site that omits it) still resolve via the room-less legacy `/sections/:id` route, which now gates on
/// the section's real room server-side rather than the caller's personal room.
export function folderUrl(id: string, roomBasePath = ""): string {
  return `${window.location.origin}${roomBasePath}/sections/${id}`;
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

/// Copy `url` to the clipboard as **rich text** — an anchor whose visible text is `label` — with a
/// plain-text fallback of the bare URL (so pasting into a rich editor shows the transcript name as a
/// link, and pasting into a plain field gives the URL). Falls back to `writeText` when the richer
/// `clipboard.write`/`ClipboardItem` API is unavailable. Returns true on success.
export async function copyRichLink(url: string, label: string): Promise<boolean> {
  const html = `<a href="${escapeHtml(url)}">${escapeHtml(label)}</a>`;
  try {
    if (
      typeof ClipboardItem !== "undefined" &&
      navigator.clipboard &&
      typeof navigator.clipboard.write === "function"
    ) {
      await navigator.clipboard.write([
        new ClipboardItem({
          "text/html": new Blob([html], { type: "text/html" }),
          "text/plain": new Blob([url], { type: "text/plain" }),
        }),
      ]);
      return true;
    }
  } catch {
    // Fall through to the plain-text path below.
  }
  try {
    await navigator.clipboard.writeText(url);
    return true;
  } catch {
    return false;
  }
}
