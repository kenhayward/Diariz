import axios from "axios";
import type {
  AdminUser,
  AuthResponse,
  ChatAttachment,
  ChatConversation,
  ChatConversationSummary,
  ChatTurn,
  ChatUsage,
  GrantResult,
  RecordingDetail,
  RecordingSource,
  RecordingSummary,
  SavedChatContext,
  SectionDto,
  SetupValidation,
  SpeakerProfile,
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

  // ---- Access requests / account setup (public) ----

  async requestAccess(email: string): Promise<void> {
    await http.post("/api/auth/request-access", { email });
  },

  async validateSetup(email: string, token: string): Promise<SetupValidation> {
    const { data } = await http.get<SetupValidation>("/api/auth/setup/validate", { params: { email, token } });
    return data;
  },

  async setup(body: { email: string; token: string; fullName: string; password: string }): Promise<AuthResponse> {
    const { data } = await http.post<AuthResponse>("/api/auth/setup", body);
    return data;
  },

  // ---- Admin user management ----

  async listUsers(): Promise<AdminUser[]> {
    const { data } = await http.get<AdminUser[]>("/api/admin/users");
    return data;
  },

  /// Admin creates a user by email and kicks off onboarding (setup link emailed, or returned to show).
  async addUser(email: string): Promise<GrantResult> {
    const { data } = await http.post<GrantResult>("/api/admin/users", { email });
    return data;
  },

  async grantUser(id: string): Promise<GrantResult> {
    const { data } = await http.post<GrantResult>(`/api/admin/users/${id}/grant`);
    return data;
  },

  async denyUser(id: string): Promise<void> {
    await http.post(`/api/admin/users/${id}/deny`);
  },

  async setUserRole(id: string, role: "Standard" | "Administrator"): Promise<void> {
    await http.put(`/api/admin/users/${id}/role`, { role });
  },

  async setUserEnabled(id: string, isEnabled: boolean): Promise<void> {
    await http.put(`/api/admin/users/${id}/enabled`, { isEnabled });
  },

  async deleteUser(id: string): Promise<void> {
    await http.delete(`/api/admin/users/${id}`);
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

  // ---- Speaker identification (voiceprints) ----

  async listSpeakerProfiles(): Promise<SpeakerProfile[]> {
    const { data } = await http.get<SpeakerProfile[]>("/api/speaker-profiles");
    return data;
  },

  /// Enrol a new person from one of a recording's diarized speakers (its embedding seeds the voiceprint).
  async createSpeakerProfile(name: string, recordingId: string, label: string): Promise<SpeakerProfile> {
    const { data } = await http.post<SpeakerProfile>("/api/speaker-profiles", { name, recordingId, label });
    return data;
  },

  /// Reassign a recording's speaker to an enrolled voiceprint, or pass null to unassign.
  async assignSpeaker(id: string, label: string, profileId: string | null): Promise<void> {
    await http.put(`/api/recordings/${id}/speakers/${encodeURIComponent(label)}/assign`, { profileId });
  },

  async deleteSpeakerProfile(profileId: string): Promise<void> {
    await http.delete(`/api/speaker-profiles/${profileId}`);
  },

  async listSections(): Promise<SectionDto[]> {
    const { data } = await http.get<SectionDto[]>("/api/sections");
    return data;
  },

  async createSection(name: string): Promise<SectionDto> {
    const { data } = await http.post<SectionDto>("/api/sections", { name });
    return data;
  },

  async renameSection(id: string, name: string): Promise<void> {
    await http.put(`/api/sections/${id}`, { name });
  },

  async deleteSection(id: string): Promise<void> {
    await http.delete(`/api/sections/${id}`);
  },

  async moveRecording(id: string, sectionId: string | null): Promise<void> {
    await http.put(`/api/recordings/${id}/section`, { sectionId });
  },

  /// Set the section + 0-based order of each listed recording in one call (drag-and-drop).
  async reorderRecordings(sectionId: string | null, orderedIds: string[]): Promise<void> {
    await http.put("/api/recordings/reorder", { sectionId, orderedIds });
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

  // ---- Chat ----

  /// Stream a chat reply. Uses raw fetch (not the axios instance) so the response body can be read
  /// incrementally; the JWT bearer must therefore be attached manually here. Resolves with the final
  /// context-usage snapshot.
  async chatStream(
    body: {
      recordingIds: string[];
      attachmentName?: string | null;
      attachmentText?: string | null;
      messages: ChatTurn[];
    },
    handlers: { onToken: (token: string) => void; onMeta?: (u: ChatUsage) => void; signal?: AbortSignal },
  ): Promise<ChatUsage> {
    const res = await fetch(`${baseURL}/api/chat/stream`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${getToken() ?? ""}` },
      body: JSON.stringify(body),
      signal: handlers.signal,
    });

    if (res.status === 401) {
      setToken(null);
      if (window.location.pathname !== "/login") window.location.assign("/login");
      throw new Error("Session expired.");
    }
    if (!res.ok || !res.body) {
      const text = await res.text().catch(() => "");
      throw new Error(text || `Chat failed (${res.status}).`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    let usage: ChatUsage = { model: "", contextUsed: 0, contextTotal: 0 };

    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      // SSE frames are separated by a blank line.
      let sep: number;
      while ((sep = buffer.indexOf("\n\n")) >= 0) {
        const frame = buffer.slice(0, sep);
        buffer = buffer.slice(sep + 2);
        const dataLine = frame.split("\n").find((l) => l.startsWith("data:"));
        if (!dataLine) continue;
        const json = dataLine.slice("data:".length).trim();
        if (!json) continue;
        const evt = JSON.parse(json) as {
          type: string;
          value?: string;
          model?: string;
          message?: string;
          contextUsed?: number;
          contextTotal?: number;
        };
        if (evt.type === "token" && evt.value) {
          handlers.onToken(evt.value);
        } else if (evt.type === "meta") {
          usage = { model: evt.model ?? "", contextUsed: evt.contextUsed ?? 0, contextTotal: evt.contextTotal ?? 0 };
          handlers.onMeta?.(usage);
        } else if (evt.type === "done") {
          usage = {
            model: evt.model ?? usage.model,
            contextUsed: evt.contextUsed ?? usage.contextUsed,
            contextTotal: evt.contextTotal ?? usage.contextTotal,
          };
        } else if (evt.type === "error") {
          throw new Error(evt.message ?? "Chat failed.");
        }
      }
    }
    return usage;
  },

  async uploadChatAttachment(file: File): Promise<ChatAttachment> {
    const form = new FormData();
    form.append("file", file);
    const { data } = await http.post<ChatAttachment>("/api/chat/attachment", form);
    return data;
  },

  async listChatConversations(): Promise<ChatConversationSummary[]> {
    const { data } = await http.get<ChatConversationSummary[]>("/api/chat/conversations");
    return data;
  },

  async getChatConversation(id: string): Promise<ChatConversation> {
    const { data } = await http.get<ChatConversation>(`/api/chat/conversations/${id}`);
    return data;
  },

  async createChatConversation(body: { messages: ChatTurn[]; context: SavedChatContext }): Promise<{ id: string; title: string }> {
    const { data } = await http.post<{ id: string; title: string }>("/api/chat/conversations", body);
    return data;
  },

  async updateChatConversation(
    id: string,
    body: { messages: ChatTurn[]; context: SavedChatContext },
  ): Promise<{ id: string; title: string }> {
    const { data } = await http.put<{ id: string; title: string }>(`/api/chat/conversations/${id}`, body);
    return data;
  },

  async deleteChatConversation(id: string): Promise<void> {
    await http.delete(`/api/chat/conversations/${id}`);
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
