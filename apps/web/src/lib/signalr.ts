import { HubConnectionBuilder, type HubConnection } from "@microsoft/signalr";
import { getToken } from "./api";
import { reconnectDelayMs } from "./signalrRetry";

const baseURL = (window as any).__DIARIZ_API_BASE__ ?? "";

export interface StatusEvent {
  recordingId: string;
  status: string;
}

export function createHub(onStatus: (e: StatusEvent) => void): HubConnection {
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

  conn.on("RecordingStatusChanged", onStatus);
  return conn;
}
