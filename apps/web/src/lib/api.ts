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
  Language,
  PlatformSettings,
  RecordingAction,
  RecordingDetail,
  RecordingSource,
  RecordingSummary,
  SavedChatContext,
  SectionDto,
  SetupValidation,
  SpeakerProfile,
  SpeakerProfileDetail,
  UpdateUserProfile,
  UpdateUserSettings,
  UserProfile,
  UserSettings,
  UserStorage,
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

  async requestAccess(email: string, fullName?: string): Promise<void> {
    await http.post("/api/auth/request-access", { email, fullName: fullName ?? null });
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
  async addUser(email: string, fullName?: string): Promise<GrantResult> {
    const { data } = await http.post<GrantResult>("/api/admin/users", { email, fullName: fullName ?? null });
    return data;
  },

  /// Raise/lower a user's storage quota (bytes), up to the platform maximum.
  async setUserQuota(id: string, quotaBytes: number): Promise<void> {
    await http.put(`/api/admin/users/${id}/quota`, { quotaBytes });
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

  /// Upload an existing audio file for transcription (the "Upload" button). The worker backfills the
  /// duration, so we send 0; the server validates the file's actual bytes + size.
  async uploadFile(file: File, title: string): Promise<RecordingSummary> {
    const form = new FormData();
    form.append("audio", file, file.name);
    form.append("title", title);
    form.append("durationMs", "0");
    form.append("source", "Upload");
    const { data } = await http.post<RecordingSummary>("/api/recordings", form);
    return data;
  },

  async renameSpeaker(id: string, label: string, displayName: string): Promise<void> {
    await http.put(`/api/recordings/${id}/speakers`, { label, displayName });
  },

  /// Edit a segment. `text` is tri-state: a string sets the revision (preserving the original);
  /// `null` resets to the model's original (clears the revision).
  async updateSegment(id: string, segmentId: string, text: string | null): Promise<void> {
    await http.put(`/api/recordings/${id}/segments/${segmentId}`, { text });
  },

  /// Delete a single segment from the current transcription (permanent for this version).
  async deleteSegment(id: string, segmentId: string): Promise<void> {
    await http.delete(`/api/recordings/${id}/segments/${segmentId}`);
  },

  /// Mark a speaker as "Multiple Speakers" (overlapping speech) — excluded from voiceprints.
  async markMultiSpeaker(id: string, label: string): Promise<void> {
    await http.put(`/api/recordings/${id}/speakers/${encodeURIComponent(label)}/multi`);
  },

  /// Manually create or edit the transcript's summary (flags it as user-edited; works without an LLM).
  async updateSummary(id: string, text: string): Promise<void> {
    await http.put(`/api/recordings/${id}/summary`, { text });
  },

  /// Translate the whole transcript (segments + summary + actions) into `language` (BCP-47), or the
  /// caller's native language when omitted. Translations land in each segment's revision.
  async translateRecording(id: string, language?: string): Promise<void> {
    await http.post(`/api/recordings/${id}/translate`, { language: language ?? null });
  },

  async translateSegment(id: string, segmentId: string, language?: string): Promise<void> {
    await http.post(`/api/recordings/${id}/segments/${segmentId}/translate`, { language: language ?? null });
  },

  /// Collapse consecutive same-speaker segments in the current transcription (permanent for this version).
  async mergeSegments(id: string): Promise<void> {
    await http.post(`/api/recordings/${id}/merge-segments`);
  },

  /// Re-run speaker identification against current voiceprints (no re-transcription).
  async reidentify(id: string): Promise<void> {
    await http.post(`/api/recordings/${id}/reidentify`);
  },

  /// Email the current transcript to the signed-in user's account address.
  async emailTranscript(id: string): Promise<void> {
    await http.post(`/api/recordings/${id}/email`);
  },

  // ---- Action items ----

  /// Run the LLM over the current transcript and replace the recording's action list (may be empty).
  async extractActions(id: string): Promise<RecordingAction[]> {
    const { data } = await http.post<RecordingAction[]>(`/api/recordings/${id}/actions/extract`);
    return data;
  },

  async createAction(
    id: string,
    body: { text: string; actor: string; deadline: string },
  ): Promise<RecordingAction> {
    const { data } = await http.post<RecordingAction>(`/api/recordings/${id}/actions`, body);
    return data;
  },

  async updateAction(
    id: string,
    actionId: string,
    body: { text?: string; actor?: string; deadline?: string },
  ): Promise<void> {
    await http.put(`/api/recordings/${id}/actions/${actionId}`, body);
  },

  async deleteAction(id: string, actionId: string): Promise<void> {
    await http.delete(`/api/recordings/${id}/actions/${actionId}`);
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

  /// People management (PR2): detail with training contributions, rename, merge, and full erase.
  async getSpeakerProfile(id: string): Promise<SpeakerProfileDetail> {
    const { data } = await http.get<SpeakerProfileDetail>(`/api/speaker-profiles/${id}`);
    return data;
  },

  async renameSpeakerProfile(id: string, name: string): Promise<void> {
    await http.put(`/api/speaker-profiles/${id}`, { name });
  },

  async removeProfileContribution(id: string, contributionId: string): Promise<void> {
    await http.delete(`/api/speaker-profiles/${id}/contributions/${contributionId}`);
  },

  async mergeSpeakerProfiles(id: string, sourceId: string): Promise<void> {
    await http.post(`/api/speaker-profiles/${id}/merge`, { sourceId });
  },

  /// GDPR: erase all of the caller's voiceprints (auto-labels revert; manual names kept).
  async deleteAllSpeakerProfiles(): Promise<void> {
    await http.delete("/api/speaker-profiles");
  },

  async listSections(): Promise<SectionDto[]> {
    const { data } = await http.get<SectionDto[]>("/api/sections");
    return data;
  },

  /// Create a section. Pass `parentId` to create a sub-section (one level of nesting).
  async createSection(name: string, parentId: string | null = null): Promise<SectionDto> {
    const { data } = await http.post<SectionDto>("/api/sections", { name, parentId });
    return data;
  },

  async renameSection(id: string, name: string): Promise<void> {
    await http.put(`/api/sections/${id}`, { name });
  },

  async deleteSection(id: string): Promise<void> {
    await http.delete(`/api/sections/${id}`);
  },

  /// Set the parent + 0-based order of each listed section in one call (drag-and-drop reorder/reparent).
  async reorderSections(parentId: string | null, orderedIds: string[]): Promise<void> {
    await http.put("/api/sections/reorder", { parentId, orderedIds });
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

  /// Delete just the audio blob (keeps the transcript + metadata, frees the quota).
  async deleteAudio(id: string): Promise<void> {
    await http.delete(`/api/recordings/${id}/audio`);
  },

  /// Bulk delete audio for the given recordings (recordings-list "Delete audio" action).
  async deleteAudioBulk(ids: string[]): Promise<void> {
    await http.post("/api/recordings/audio/delete", { ids });
  },

  /// Merge 2+ recordings into the earliest-created one (transcripts + audio). Async on the worker.
  async mergeRecordings(ids: string[]): Promise<void> {
    await http.post("/api/recordings/merge", { ids });
  },

  async summarize(id: string): Promise<void> {
    await http.post(`/api/recordings/${id}/summarize`);
  },

  // ---- Languages & profile (localization) ----

  async getLanguages(): Promise<Language[]> {
    const { data } = await http.get<Language[]>("/api/languages");
    return data;
  },

  async getProfile(): Promise<UserProfile> {
    const { data } = await http.get<UserProfile>("/api/user/profile");
    return data;
  },

  /// Update the signed-in user's profile. Returns a fresh access token (the new display name lives in
  /// the JWT) — the caller should adopt it via the auth context.
  async updateProfile(body: UpdateUserProfile): Promise<AuthResponse> {
    const { data } = await http.put<AuthResponse>("/api/user/profile", body);
    return data;
  },

  async getUserSettings(): Promise<UserSettings> {
    const { data } = await http.get<UserSettings>("/api/user/settings");
    return data;
  },

  async updateUserSettings(body: UpdateUserSettings): Promise<void> {
    await http.put("/api/user/settings", body);
  },

  // ---- Storage quotas ----

  async getUserStorage(): Promise<UserStorage> {
    const { data } = await http.get<UserStorage>("/api/user/storage");
    return data;
  },

  async getPlatformSettings(): Promise<PlatformSettings> {
    const { data } = await http.get<PlatformSettings>("/api/platform/settings");
    return data;
  },

  async updatePlatformSettings(body: PlatformSettings): Promise<PlatformSettings> {
    const { data } = await http.put<PlatformSettings>("/api/platform/settings", body);
    return data;
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

  /// Re-transcribe. `speakers` is tri-state: omit to keep the recording's existing diarization hints,
  /// or pass an object (null bounds = automatic) to set them.
  async retranscribe(
    id: string,
    opts?: { model?: string | null; speakers?: { min: number | null; max: number | null } },
  ): Promise<void> {
    await http.post(`/api/recordings/${id}/retranscribe`, {
      model: opts?.model ?? null,
      speakers: opts?.speakers ?? null,
    });
  },

  async audioUrl(id: string, opts?: { download?: boolean }): Promise<string> {
    const { data } = await http.get<{ url: string }>(`/api/recordings/${id}/audio-url`, {
      params: opts?.download ? { download: true } : undefined,
    });
    // The API returns a same-origin relative path (`/api/recordings/.../audio?...`). Prefix the axios
    // baseURL so it resolves against the API in the Electron shell (where the SPA isn't same-origin).
    return baseURL + data.url;
  },

  /// Transcript endpoints are JWT-protected, so fetch the bytes (carrying the bearer) and
  /// trigger a client-side download rather than navigating to a bare URL.
  async downloadTranscript(id: string, format: "txt" | "md" | "rtf"): Promise<void> {
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
