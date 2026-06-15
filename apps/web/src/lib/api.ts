import axios from "axios";
import type {
  AuthResponse,
  RecordingDetail,
  RecordingSummary,
} from "./types";

const TOKEN_KEY = "diariz.token";

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}
export function setToken(token: string | null) {
  if (token) localStorage.setItem(TOKEN_KEY, token);
  else localStorage.removeItem(TOKEN_KEY);
}

// In the browser dev server, "/api" is proxied to the backend. The Electron shell
// can override the base URL via window.__DIARIZ_API_BASE__.
const baseURL = (window as any).__DIARIZ_API_BASE__ ?? "";

export const http = axios.create({ baseURL });

http.interceptors.request.use((config) => {
  const token = getToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

export const api = {
  async login(email: string, password: string): Promise<AuthResponse> {
    const { data } = await http.post<AuthResponse>("/api/auth/login", { email, password });
    return data;
  },

  async listRecordings(): Promise<RecordingSummary[]> {
    const { data } = await http.get<RecordingSummary[]>("/api/recordings");
    return data;
  },

  async getRecording(id: string): Promise<RecordingDetail> {
    const { data } = await http.get<RecordingDetail>(`/api/recordings/${id}`);
    return data;
  },

  async upload(blob: Blob, title: string, durationMs: number): Promise<RecordingSummary> {
    const form = new FormData();
    const ext = blob.type.includes("wav") ? "wav" : "webm";
    form.append("audio", blob, `recording.${ext}`);
    form.append("title", title);
    form.append("durationMs", String(Math.round(durationMs)));
    const { data } = await http.post<RecordingSummary>("/api/recordings", form);
    return data;
  },

  async renameSpeaker(id: string, label: string, displayName: string): Promise<void> {
    await http.put(`/api/recordings/${id}/speakers`, { label, displayName });
  },

  async retranscribe(id: string, model?: string): Promise<void> {
    await http.post(`/api/recordings/${id}/retranscribe`, { model: model ?? null });
  },

  async audioUrl(id: string): Promise<string> {
    const { data } = await http.get<{ url: string }>(`/api/recordings/${id}/audio-url`);
    return data.url;
  },
};
