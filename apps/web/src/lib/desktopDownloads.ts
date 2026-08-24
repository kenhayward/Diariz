// Bridge to the Electron shell's download handler. The desktop preload injects
// `window.diariz.onDownloadEvent`; in a plain browser it is absent and everything here degrades to a
// no-op, because the browser's own download shelf already does this job.
//
// The shell sends raw byte counts and leaves the arithmetic and the wording here, where `formatBytes`
// and the locale catalogs already are.

import { formatBytes } from "./format";

export type DesktopDownloadState = "completed" | "cancelled" | "interrupted";

export interface DesktopDownloadEvent {
  type: "started" | "progress" | "done";
  id: number;
  url: string;
  filename: string;
  receivedBytes: number;
  totalBytes: number;
  /// Present on "done" only.
  state?: DesktopDownloadState;
  /// Where the file landed. Present on "done" only, and empty when the user cancelled the Save dialog.
  savePath?: string;
}

export interface DesktopDownloadBridge {
  onDownloadEvent?: (cb: (e: DesktopDownloadEvent) => void) => () => void;
}

/// Subscribe to the downloads this caller cares about. The shell's handler is generic - audio, transcript
/// and formula-result downloads all arrive here - so `matchUrl` decides which are yours.
/// Returns a disposer, which is safe to call even with no bridge.
export function watchDesktopDownloads(
  bridge: DesktopDownloadBridge | undefined,
  matchUrl: (url: string) => boolean,
  onEvent: (e: DesktopDownloadEvent) => void,
): () => void {
  if (!bridge?.onDownloadEvent) return () => {};
  return bridge.onDownloadEvent((e) => {
    if (matchUrl(e.url)) onEvent(e);
  });
}

export interface DownloadProgress {
  phase: "starting" | "progressing";
  /// Null when the response carried no Content-Length, so there is no percentage to show.
  percent: number | null;
  /// The total when it is known, otherwise how much has arrived.
  sizeText: string;
}

/// Turn raw byte counts into the numbers a progress line needs. Derived from the counts rather than from
/// an assumed sequence of events: Electron fires will-download before the Save-As dialog resolves, so
/// "started but no bytes yet" can last as long as the user takes to pick a folder.
export function downloadProgress({
  receivedBytes,
  totalBytes,
}: {
  receivedBytes: number;
  totalBytes: number;
}): DownloadProgress {
  if (!(receivedBytes > 0)) {
    return { phase: "starting", percent: null, sizeText: formatBytes(Math.max(totalBytes, 0)) };
  }
  if (!(totalBytes > 0)) {
    return { phase: "progressing", percent: null, sizeText: formatBytes(receivedBytes) };
  }
  // Chromium can report a received count a shade over the total; 101% reads as a bug.
  const percent = Math.min(100, Math.round((receivedBytes / totalBytes) * 100));
  return { phase: "progressing", percent, sizeText: formatBytes(totalBytes) };
}
