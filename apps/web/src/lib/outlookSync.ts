/// The seam between the Electron shell's Outlook reader and the web app. Mirrors trayRecorder/trayScreenshots:
/// the shell owns the thing only it can reach (here, the local Outlook calendar over COM), and the web app owns
/// the HTTP - it holds the signed-in user's token, the shell never does. Everything here is a no-op in a plain
/// browser, so callers never have to branch on `isElectron`.

import type { OutlookEventInput } from "./types";

/// One harvested window, handed over for upload. `complete` is false when the shell's enumeration threw
/// partway: the server upserts what arrived but refuses to treat the gaps as deletions.
export interface OutlookPushPayload {
  syncId: string;
  device: { deviceId: string; deviceName?: string; mailboxName?: string; timeZone?: string };
  windowStart: string;
  windowEnd: string;
  complete: boolean;
  events: OutlookEventInput[];
}

/// What the renderer reports back so the tray label and notification can be honest about the outcome.
export interface OutlookPushResult {
  ok: boolean;
  created?: number;
  updated?: number;
  deleted?: number;
  eventCount?: number;
  error?: string;
}

/// The shell's own view of where a sync has got to, so on-screen buttons can show a spinner and disable
/// themselves without the web app having to track it.
export interface OutlookShellState {
  phase: "idle" | "reading" | "pushing";
  lastSyncAt?: number;
  lastError?: string | null;
  lastCount?: number;
}

/// What the web app tells the shell about the connector: whether the user has opted in, and the window and
/// privacy choices to read with. Re-sent whenever the settings change, and on mount - which is also the
/// shell's cue that a signed-in renderer is ready to POST, and therefore that a launch sync can run.
export interface OutlookReadyConfig {
  enabled: boolean;
  pastDays: number;
  futureDays: number;
  skipPrivate: boolean;
  includeBody: boolean;
}

interface OutlookShell {
  canSyncOutlook?: boolean;
  outlookAvailable?: () => Promise<boolean>;
  reportOutlookReady?: (cfg: OutlookReadyConfig) => void;
  syncOutlookNow?: () => Promise<{ started: boolean; reason?: string }>;
  onOutlookPush?: (cb: (payload: OutlookPushPayload) => void) => () => void;
  reportOutlookResult?: (result: OutlookPushResult) => void;
  onOutlookState?: (cb: (state: OutlookShellState) => void) => () => void;
}

function shell(): OutlookShell | undefined {
  return (window as { diariz?: OutlookShell }).diariz;
}

/// True when this build could sync Outlook at all - the Windows desktop shell. Drives whether the UI offers
/// the affordance; a browser or a Mac shows the explanation instead.
export function canSyncOutlook(): boolean {
  return shell()?.canSyncOutlook === true;
}

/// Whether the shell can actually reach Outlook right now (installed, classic rather than the new Outlook, not
/// blocked). False in a browser, and false on an older shell that predates the bridge.
export async function outlookAvailable(): Promise<boolean> {
  const api = shell();
  if (!api?.outlookAvailable) return false;
  try {
    return await api.outlookAvailable();
  } catch {
    return false; // a shell that errors answering is, for our purposes, unavailable
  }
}

export function reportOutlookReady(cfg: OutlookReadyConfig): void {
  shell()?.reportOutlookReady?.(cfg);
}

/// Ask the shell to sync now. `started: false` carries a reason the caller can explain (`unavailable`,
/// `disabled`, `busy`, `cooldown`, ...).
export async function syncOutlookNow(): Promise<{ started: boolean; reason?: string }> {
  const api = shell();
  if (!api?.syncOutlookNow) return { started: false, reason: "unavailable" };
  return api.syncOutlookNow();
}

/// Subscribe to harvested windows. Returns an unsubscribe function (a no-op in a browser).
export function onOutlookPush(cb: (payload: OutlookPushPayload) => void): () => void {
  const api = shell();
  if (!api?.onOutlookPush) return () => {};
  return api.onOutlookPush(cb);
}

export function reportOutlookResult(result: OutlookPushResult): void {
  shell()?.reportOutlookResult?.(result);
}

/// Subscribe to the shell's sync phase. Returns an unsubscribe function (a no-op in a browser).
export function onOutlookState(cb: (state: OutlookShellState) => void): () => void {
  const api = shell();
  if (!api?.onOutlookState) return () => {};
  return api.onOutlookState(cb);
}

/// Split a harvested window into pages. Chunking is the web app's job because it is an HTTP concern - the
/// shell hands over the whole window in one structured-clone message and does not care how it is uploaded.
export function chunkEvents<T>(events: readonly T[], size: number): T[][] {
  if (size <= 0) throw new Error("chunk size must be positive");
  // A run with no events is still a run: it needs one (final) page so the server can sweep meetings that were
  // cancelled since last time. Returning zero pages would leave them mirrored forever.
  if (events.length === 0) return [[]];

  const pages: T[][] = [];
  for (let i = 0; i < events.length; i += size) pages.push(events.slice(i, i + size));
  return pages;
}
