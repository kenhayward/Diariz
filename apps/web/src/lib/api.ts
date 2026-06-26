import axios from "axios";
import type {
  AuthResponse,
  RecordingDetail,
  RecordingSource,
  RecordingSummary,
  UpdateUserSettings,
  UserSettings,
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

/// On an expired/invalid token the API returns 401. Clear the stale token and send the user to
/// login rather than leaving them on a silently-empty page. Exported for testing.
export function handleAuthError(error: unknown): void {
  if (axios.isAxiosError(error) && error.response?.status === 401) {
    setToken(null);
    if (window.location.pathname !== "/login") window.location.assign("/login");
  }
}

http.interceptors.response.use(
  (response) => response,
  (error) => {
    handleAuthError(error);
    return Promise.reject(error);
  },
);

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

  async upload(
    blob: Blob,
    title: string,
    durationMs: number,
    source: RecordingSource = "Microphone",
  ): Promise<RecordingSummary> {
    const form = new FormData();
    const ext = blob.type.includes("wav") ? "wav" : "webm";
    form.append("audio", blob, `recording.${ext}`);
    form.append("title", title);
    form.append("durationMs", String(Math.round(durationMs)));
    form.append("source", source);
    const { data } = await http.post<RecordingSummary>("/api/recordings", form);
    return data;
  },

  async renameSpeaker(id: string, label: string, displayName: string): Promise<void> {
    await http.put(`/api/recordings/${id}/speakers`, { label, displayName });
  },

  async updateSegment(id: string, segmentId: string, text: string): Promise<void> {
    await http.put(`/api/recordings/${id}/segments/${segmentId}`, { text });
  },

  async renameRecording(id: string, name: string | null): Promise<void> {
    await http.put(`/api/recordings/${id}/name`, { name });
  },

  async deleteRecording(id: string): Promise<void> {
    await http.delete(`/api/recordings/${id}`);
  },

  async summarize(id: string): Promise<void> {
    await http.post(`/api/recordings/${id}/summarize`);
  },

  async getUserSettings(): Promise<UserSettings> {
    const { data } = await http.get<UserSettings>("/api/user/settings");
    return data;
  },

  async updateUserSettings(body: UpdateUserSettings): Promise<void> {
    await http.put("/api/user/settings", body);
  },

  async retranscribe(id: string, model?: string): Promise<void> {
    await http.post(`/api/recordings/${id}/retranscribe`, { model: model ?? null });
  },

  async audioUrl(id: string, opts?: { download?: boolean }): Promise<string> {
    const { data } = await http.get<{ url: string }>(`/api/recordings/${id}/audio-url`, {
      params: opts?.download ? { download: true } : undefined,
    });
    return data.url;
  },

  /// Transcript endpoints are JWT-protected, so fetch the bytes (carrying the bearer) and
  /// trigger a client-side download rather than navigating to a bare URL.
  async downloadTranscript(id: string, format: "txt" | "srt"): Promise<void> {
    const { data, headers } = await http.get(`/api/recordings/${id}/transcript.${format}`, {
      responseType: "blob",
    });
    triggerBlobDownload(data as Blob, filenameFromHeaders(headers, `transcript.${format}`));
  },

  /// The audio download URL is a self-authenticating presigned URL with attachment disposition.
  async downloadAudio(id: string): Promise<void> {
    const url = await api.audioUrl(id, { download: true });
    const a = document.createElement("a");
    a.href = url;
    a.rel = "noopener";
    document.body.appendChild(a);
    a.click();
    a.remove();
  },
};

function filenameFromHeaders(headers: unknown, fallback: string): string {
  const cd = (headers as Record<string, string> | undefined)?.["content-disposition"];
  const match = cd?.match(/filename\*?=(?:UTF-8'')?"?([^";]+)"?/i);
  return match ? decodeURIComponent(match[1]) : fallback;
}

function triggerBlobDownload(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// Extract a human-readable message from an axios error. Understands the shapes the
// API returns: plain strings (BadRequest("...")), Identity error arrays, and
// ASP.NET ProblemDetails ({ title, errors }). Falls back gracefully.
export function apiErrorMessage(e: unknown, fallback = "Something went wrong."): string {
  if (axios.isAxiosError(e)) {
    if (!e.response) return "Cannot reach the server.";
    const data = e.response.data as any;
    if (typeof data === "string" && data.trim()) return data;
    if (Array.isArray(data) && data.length) return data.map(String).join(" ");
    if (data?.errors) {
      const msgs = Object.values(data.errors).flat();
      if (msgs.length) return msgs.map(String).join(" ");
    }
    if (data?.title) return data.title;
    return `Request failed (${e.response.status}).`;
  }
  return e instanceof Error ? e.message : fallback;
}
