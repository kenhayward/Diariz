import { HubConnectionBuilder, type HubConnection } from "@microsoft/signalr";
import { getToken } from "./api";
import { reconnectDelayMs } from "./signalrRetry";

const baseURL = (window as any).__DIARIZ_API_BASE__ ?? "";

export interface StatusEvent {
  recordingId: string;
  status: string;
}

/// One chunk of a running meeting has been transcribed. Carries ids rather than text: the page
/// refetches, so one event shape serves an append, a correction and (later) a relabel alike.
export interface LiveTranscriptEvent {
  recordingId: string;
  transcriptionId: string;
  sequence: number;
}

/// The server has stopped transcribing live for this recording. Capture is unaffected.
export interface LiveTranscriptDegradedEvent {
  recordingId: string;
  sequence: number;
}

export interface HubHandlers {
  onStatus: (e: StatusEvent) => void;
  onLiveTranscript?: (e: LiveTranscriptEvent) => void;
  onLiveTranscriptDegraded?: (e: LiveTranscriptDegradedEvent) => void;
}

export function createHub(
  handlers: ((e: StatusEvent) => void) | HubHandlers,
): HubConnection {
  // Accepts the original single-callback form as well as the handler bag, so every existing caller and
  // its tests keep working unchanged - this is a hub used from several places.
  const h: HubHandlers = typeof handlers === "function" ? { onStatus: handlers } : handlers;
  return buildHub(h);
}

function buildHub(handlers: HubHandlers): HubConnection {
  const conn = new HubConnectionBuilder()
    .withUrl(`${baseURL}/hubs/transcription`, {
      accessTokenFactory: () => getToken() ?? "",
    })
    // An explicit policy rather than the 4-attempt default, which gives up after ~42s - less than an API
    // restart takes, so a redeploy silently killed the hub for the rest of the session. See signalrRetry.
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) => reconnectDelayMs(ctx.previousRetryCount),
    })
    .build();

  conn.on("RecordingStatusChanged", handlers.onStatus);
  if (handlers.onLiveTranscript) conn.on("LiveTranscriptAppended", handlers.onLiveTranscript);
  if (handlers.onLiveTranscriptDegraded)
    conn.on("LiveTranscriptDegraded", handlers.onLiveTranscriptDegraded);
  return conn;
}
