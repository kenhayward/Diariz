import { HubConnectionBuilder, type HubConnection } from "@microsoft/signalr";
import { getToken } from "./api";

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
    .withAutomaticReconnect()
    .build();

  conn.on("RecordingStatusChanged", onStatus);
  return conn;
}
