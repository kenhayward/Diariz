import axios from "axios";
import { record } from "./trail";

// A per-request opt-out of the global 401→/login redirect (used by the silent token refresh).
declare module "axios" {
  export interface AxiosRequestConfig {
    skipAuthRedirect?: boolean;
  }
}

import type {
  TemplateContent,
  ChatModelOption,
  AdminUser,
  Attachment,
  AttachmentDraft,
  AuthResponse,
  CalendarEvent,
  CalendarMatch,
  CalendarLink,
  IcsFeed,
  IcsFeedInput,
  OutlookSource,
  OutlookSourceInput,
  OutlookSyncRequest,
  OutlookSyncResult,
  ChatAttachment,
  ChatConversation,
  ChatConversationSummary,
  ChatTurn,
  ChatUsage,
  Formula,
  FormulaResult,
  SectionFormulaResult,
  FormulaScope,
  SharedFormula,
  GrantResult,
  Language,
  McpToken,
  McpTokenCreated,
  ApiToken,
  ApiTokenCreated,
  CreateApiTokenOptions,
  MeetingType,
  MeetingTypeInput,
  OAuthConnection,
  PlatformSettings,
  RecordingAction,
  ActionListItem,
  MeetingNote,
  RecordingDetail,
  RecordingSource,
  RecordingSummary,
  RoomListItem,
  RoomDetail,
  RoomMemberInput,
  RoomInput,
  SavedChatContext,
  Screenshot,
  SectionDto,
  SectionDetail,
  SeriesRecording,
  SectionNoteItem,
  SectionAttachmentItem,
  TagCloudEntry,
  SetupValidation,
  Person,
  PersonDetail,
  PersonDuplicateGroup,
  GoogleCalendarListItem,
  Group,
  UpdateUserProfile,
  RestoreResult,
  BackupStatus,
  UpdateUserSettings,
  UserProfile,
  UserSettings,
  UserStorage,
  SearchResponse,
  WebhookSubscription,
  WebhookCreated,
  WebhookDelivery,
  CreateWebhookBody,
  UpdateWebhookBody,
  CreatePlatformWebhookBody,
  WorkflowSignal,
  FeedbackDto,
  CreateWorkflowSignalBody,
  UpdateWorkflowSignalBody,
  LlmUsageFilter,
  LlmUsagePage,
  LlmUsageCallRow,
  LlmUsageOperationRow,
  LlmUsageSummary,
  LlmUsageGroupDimension,
  LlmUsageSortKey,
  LlmUsageFilterOptions,
  LlmModel,
  LlmModelUpsert,
  LlmAssignments,
  LlmTestOutcome,
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
/// login rather than leaving them on a silently-empty page. Exported for testing. A request may opt out
/// of the redirect (config `skipAuthRedirect`) — used by the silent token refresh, which must fail quietly.
export function handleAuthError(error: unknown): void {
  if (!axios.isAxiosError(error) || error.response?.status !== 401) return;
  if (error.config?.skipAuthRedirect) return;
  setToken(null);
  if (window.location.pathname !== "/login") window.location.assign("/login");
}

/// Feeds the feedback trail. Recording must never change what the caller sees: the success path still
/// returns the response untouched, and the error path still rejects. For "this control was in the wrong
/// state", the response status and path are usually what actually diagnoses it - which is why this seam
/// is worth more to a report than a click listener would be.
http.interceptors.response.use(
  (response) => {
    record({
      kind: "api",
      label: `${(response.config.method ?? "get").toUpperCase()} ${response.config.url ?? ""}`,
      detail: { status: response.status },
    });
    return response;
  },
  (error) => {
    if (axios.isAxiosError(error) && error.config) {
      record({
        kind: "api",
        label: `${(error.config.method ?? "get").toUpperCase()} ${error.config.url ?? ""}`,
        detail: { status: error.response?.status ?? 0 },
      });
    }
    handleAuthError(error);
    return Promise.reject(error);
  },
);

export const api = {
  async login(email: string, password: string): Promise<AuthResponse> {
    const { data } = await http.post<AuthResponse>("/api/auth/login", { email, password });
    return data;
  },

  /// OAuth consent (MCP web connector): the display name of the client requesting access, shown on the
  /// consent screen. Rejects (404) if the client id is unknown.
  async oauthConsentInfo(clientId: string): Promise<{ clientName: string }> {
    const { data } = await http.get<{ clientName: string }>("/api/oauth/consent-info", { params: { clientId } });
    return data;
  },

  /// Record the signed-in user's allow/deny decision for an OAuth client into the server-side consent cookie.
  /// The caller then navigates the browser to /connect/authorize to complete the flow.
  async oauthConsent(clientId: string, allow: boolean): Promise<void> {
    await http.post("/api/oauth/consent", { clientId, allow });
  },

  /// Re-issue the access token (sliding session). Skips the global 401 redirect so an expired-token
  /// refresh fails quietly — the next real request handles auth.
  async refresh(): Promise<AuthResponse> {
    const { data } = await http.post<AuthResponse>("/api/auth/refresh", null, { skipAuthRedirect: true });
    return data;
  },

  /// Which external sign-in providers the server has configured (public). Used to show/hide the
  /// "Sign in with Google" button on the login page.
  async getAuthProviders(): Promise<{ google: boolean }> {
    const { data } = await http.get<{ google: boolean }>("/api/auth/providers");
    return data;
  },

  /// After a successful Google sign-in the API leaves the access token in a one-time HttpOnly cookie; the
  /// callback page swaps it for the token here (a JSON body, so no proxy can strip it). Returns null if the
  /// handoff cookie is absent/expired. Skips the global 401 redirect so we can show a friendly error.
  async exchangeGoogleToken(): Promise<string | null> {
    try {
      const { data } = await http.post<{ accessToken: string }>(
        "/api/auth/google/exchange", null, { withCredentials: true, skipAuthRedirect: true });
      return data.accessToken ?? null;
    } catch {
      return null;
    }
  },

  /// Begin the incremental Google data-access consent (Calendar read) for the signed-in user.
  /// Returns Google's consent URL; the caller navigates the whole page to it.
  async connectGoogle(scopes: { calendar: boolean }): Promise<string> {
    const { data } = await http.post<{ authorizationUrl: string }>("/api/auth/google/connect", scopes);
    return data.authorizationUrl;
  },

  /// Revoke Google data access and clear the stored connection.
  async disconnectGoogle(): Promise<void> {
    await http.post("/api/auth/google/disconnect");
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

  // ---- Groups (platform permissions) ----

  async listGroups(): Promise<Group[]> {
    const { data } = await http.get<Group[]>("/api/groups");
    return data;
  },

  async createGroup(g: Pick<Group, "name"> & Partial<Pick<Group, "description" | "icon" | "color" | "permissions">>): Promise<Group> {
    const { data } = await http.post<Group>("/api/groups", { permissions: 0, ...g });
    return data;
  },

  async updateGroup(id: string, g: Pick<Group, "name" | "permissions"> & Partial<Pick<Group, "description" | "icon" | "color">>): Promise<void> {
    await http.put(`/api/groups/${id}`, g);
  },

  async deleteGroup(id: string): Promise<void> {
    await http.delete(`/api/groups/${id}`);
  },

  async addGroupMember(id: string, userId: string): Promise<void> {
    await http.put(`/api/groups/${id}/members/${userId}`);
  },

  async removeGroupMember(id: string, userId: string): Promise<void> {
    await http.delete(`/api/groups/${id}/members/${userId}`);
  },

  async setUserEnabled(id: string, isEnabled: boolean): Promise<void> {
    await http.put(`/api/admin/users/${id}/enabled`, { isEnabled });
  },

  async deleteUser(id: string): Promise<void> {
    await http.delete(`/api/admin/users/${id}`);
  },

  /// The recordings of a room (its personal room by default) - the room being browsed in the switcher.
  async listRecordings(roomId?: string | null): Promise<RecordingSummary[]> {
    const { data } = await http.get<RecordingSummary[]>("/api/recordings", {
      params: roomId ? { roomId } : undefined,
    });
    return data;
  },

  /// The rooms the signed-in user belongs to (today: just their Personal room), personal first.
  async listRooms(): Promise<RoomListItem[]> {
    const { data } = await http.get<RoomListItem[]>("/api/rooms");
    return data;
  },

  /// A shared room with its membership (members only), for the Manage Rooms editor.
  async getRoom(id: string): Promise<RoomDetail> {
    const { data } = await http.get<RoomDetail>(`/api/rooms/${id}`);
    return data;
  },

  async createRoom(input: RoomInput): Promise<{ id: string }> {
    const { data } = await http.post<{ id: string }>("/api/rooms", input);
    return data;
  },

  async updateRoom(id: string, input: RoomInput): Promise<void> {
    await http.put(`/api/rooms/${id}`, input);
  },

  async deleteRoom(id: string): Promise<void> {
    await http.delete(`/api/rooms/${id}`);
  },

  /// Upsert a member's permission grid on a room.
  async setRoomMember(id: string, m: RoomMemberInput): Promise<void> {
    await http.put(`/api/rooms/${id}/members`, m);
  },

  async removeRoomMember(id: string, principalType: number, principalId: string): Promise<void> {
    await http.delete(`/api/rooms/${id}/members/${principalType}/${principalId}`);
  },

  /// Share a recording from one room into another (a non-main placement in the target).
  async shareRecordingToRoom(id: string, fromRoomId: string, toRoomId: string): Promise<void> {
    await http.post(`/api/recordings/${id}/share`, { fromRoomId, toRoomId });
  },

  /// Unshare a recording from a room (never its home room).
  async removeRecordingFromRoom(roomId: string, id: string): Promise<void> {
    await http.delete(`/api/rooms/${roomId}/recordings/${id}`);
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
    sectionId: string | null = null,
    roomId: string | null = null,
    /// Wall clock (epoch ms) the capture began and stopped. Trailing and optional so existing callers and
    /// their positional-argument assertions are unaffected.
    times: { startedAt?: number; endedAt?: number } = {},
  ): Promise<RecordingSummary> {
    const form = new FormData();
    const ext = blob.type.includes("wav") ? "wav" : "webm";
    form.append("audio", blob, `recording.${ext}`);
    form.append("title", title);
    form.append("durationMs", String(Math.round(durationMs)));
    form.append("source", source);
    // A folder in the recorder's personal room (validated server-side); omitted = ungrouped.
    if (sectionId) form.append("sectionId", sectionId);
    // A shared room to also share the recording into; omitted = a plain personal recording.
    if (roomId) form.append("roomId", roomId);
    // What meeting matching spans from. durationMs is recorded-audio length (pauses excluded), so it cannot
    // stand in for the wall-clock end; the server falls back to upload time when these are absent.
    if (times.startedAt) form.append("startedAt", new Date(times.startedAt).toISOString());
    if (times.endedAt) form.append("endedAt", new Date(times.endedAt).toISOString());
    const { data } = await http.post<RecordingSummary>("/api/recordings", form);
    return data;
  },

  /// Upload an existing audio file for transcription (the "Upload" button). The worker backfills the
  /// duration, so we send 0; the server validates the file's actual bytes + size.
  async uploadFile(
    file: File,
    title: string,
    roomId: string | null = null,
    sectionId: string | null = null,
  ): Promise<RecordingSummary> {
    const form = new FormData();
    form.append("audio", file, file.name);
    form.append("title", title);
    form.append("durationMs", "0");
    form.append("source", "Upload");
    // When uploading while viewing a shared room, also share the recording into it (the main placement stays
    // in the uploader's personal room); omitted for a plain personal upload.
    if (roomId) form.append("roomId", roomId);
    // A folder in the uploader's personal room, from the placement preference (validated server-side, which
    // also ignores it for a shared-room upload); omitted = ungrouped.
    if (sectionId) form.append("sectionId", sectionId);
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

  /// Delete several selected segments at once (survivors renumbered server-side).
  async deleteSegments(id: string, segmentIds: string[]): Promise<void> {
    await http.post(`/api/recordings/${id}/segments/delete`, { ids: segmentIds });
  },

  /// Mark a speaker as "Multiple Speakers" (overlapping speech) — excluded from voiceprints.
  async markMultiSpeaker(id: string, label: string): Promise<void> {
    await http.put(`/api/recordings/${id}/speakers/${encodeURIComponent(label)}/multi`);
  },

  /// Manually create or edit the transcript's summary (flags it as user-edited; works without an LLM).
  async updateSummary(id: string, text: string): Promise<void> {
    await http.put(`/api/recordings/${id}/summary`, { text });
  },

  /// Re-create the meeting minutes via the LLM (overwrites a hand-edited copy; the UI warns first).
  async generateMeetingMinutes(id: string): Promise<void> {
    await http.post(`/api/recordings/${id}/meeting-minutes/generate`);
  },

  /// The meeting types (minutes templates) available to the user - Platform types plus their own.
  async listMeetingTypes(): Promise<MeetingType[]> {
    const { data } = await http.get<MeetingType[]>("/api/meeting-types");
    return data;
  },

  /// Choose the meeting type driving a recording's minutes (null = the General default) and re-run them.
  async applyMeetingType(id: string, meetingTypeId: string | null): Promise<void> {
    await http.post(`/api/recordings/${id}/meeting-type`, { meetingTypeId });
  },

  /// Create a meeting type (Personal, or Platform for admins). Returns the saved type.
  async createMeetingType(input: MeetingTypeInput): Promise<MeetingType> {
    const { data } = await http.post<MeetingType>("/api/meeting-types", input);
    return data;
  },

  /// Replace a meeting type's fields + template atomically.
  async updateMeetingType(id: string, input: MeetingTypeInput): Promise<MeetingType> {
    const { data } = await http.put<MeetingType>(`/api/meeting-types/${id}`, input);
    return data;
  },

  /// Delete a meeting type (recordings using it fall back to the General default).
  async deleteMeetingType(id: string): Promise<void> {
    await http.delete(`/api/meeting-types/${id}`);
  },

  /// Manually edit the meeting minutes (Markdown; flags them user-edited; works without an LLM).
  async updateMeetingMinutes(id: string, text: string): Promise<void> {
    await http.put(`/api/recordings/${id}/meeting-minutes`, { text });
  },

  /// Email just the meeting minutes to the signed-in user, optionally attaching the recording's files.
  async emailMeetingMinutes(id: string, includeAttachments: boolean): Promise<void> {
    await http.post(`/api/recordings/${id}/meeting-minutes/email`, { includeAttachments });
  },

  /// The Google Calendar meeting this recording most likely overlaps (by time), or null if none.
  /// Requires the user to have granted Calendar access in Preferences.
  async getCalendarMatch(id: string): Promise<CalendarMatch | null> {
    const { data } = await http.get<{ match: CalendarMatch | null }>(`/api/recordings/${id}/calendar-match`);
    return data.match;
  },

  /// The user's Google Calendar events in a date window (for the recordings Calendar tab). Returns an
  /// empty list when Calendar isn't connected. Requires the user to have granted Calendar access.
  async getCalendarEvents(timeMin: string, timeMax: string): Promise<CalendarEvent[]> {
    const { data } = await http.get<CalendarEvent[]>("/api/calendar/events", { params: { timeMin, timeMax } });
    return data;
  },

  /// A single calendar event by id, with the full invite details (attendees, description, location,
  /// organiser). Throws 404 when the event is gone or Calendar isn't connected.
  async getCalendarEvent(eventId: string): Promise<CalendarEvent> {
    const { data } = await http.get<CalendarEvent>(`/api/calendar/events/${encodeURIComponent(eventId)}`);
    return data;
  },

  /// Your other recordings of the same recurring meeting, newest occurrence first (max 10). Empty for a
  /// one-off or a series you have not recorded before; 404 when the event is gone.
  async getSeriesRecordings(eventId: string): Promise<SeriesRecording[]> {
    const { data } = await http.get<SeriesRecording[]>(
      `/api/calendar/events/${encodeURIComponent(eventId)}/recordings`);
    return data;
  },

  /// Persistently link a recording to a calendar event (manual = the user picked it by hand). Pass the
  /// event's calendarId when known so the server targets the right calendar without searching.
  async putCalendarLink(id: string, eventId: string, manual: boolean, calendarId?: string | null): Promise<CalendarLink> {
    const { data } = await http.put<CalendarLink>(`/api/recordings/${id}/calendar-link`, { eventId, manual, calendarId });
    return data;
  },

  /// Remove a recording's calendar link (idempotent).
  async deleteCalendarLink(id: string): Promise<void> {
    await http.delete(`/api/recordings/${id}/calendar-link`);
  },

  // ---- External .ics calendar feeds ----
  async listCalendarFeeds(): Promise<IcsFeed[]> {
    const { data } = await http.get<IcsFeed[]>("/api/calendar/feeds");
    return data;
  },
  /// Add a feed. The server validates + test-fetches the URL, so this rejects (400) an unreachable/unsafe URL.
  async createCalendarFeed(input: IcsFeedInput): Promise<IcsFeed> {
    const { data } = await http.post<IcsFeed>("/api/calendar/feeds", input);
    return data;
  },
  async updateCalendarFeed(id: string, input: IcsFeedInput): Promise<IcsFeed> {
    const { data } = await http.put<IcsFeed>(`/api/calendar/feeds/${id}`, input);
    return data;
  },
  async deleteCalendarFeed(id: string): Promise<void> {
    await http.delete(`/api/calendar/feeds/${id}`);
  },

  // ---- Desktop Outlook mirror ----
  /// The machines that have connected their local Outlook calendar. Empty is the normal answer.
  async listOutlookSources(): Promise<OutlookSource[]> {
    const { data } = await http.get<OutlookSource[]>("/api/calendar/outlook/sources");
    return data;
  },
  async updateOutlookSource(id: string, input: OutlookSourceInput): Promise<OutlookSource> {
    const { data } = await http.put<OutlookSource>(`/api/calendar/outlook/sources/${id}`, input);
    return data;
  },
  /// Disconnect a device. This deletes every meeting mirrored from it - it is the erase control.
  async deleteOutlookSource(id: string): Promise<void> {
    await http.delete(`/api/calendar/outlook/sources/${id}`);
  },
  /// Push one page of a harvested window. The desktop shell harvests; the web app uploads, because it is the
  /// side holding the signed-in user's token.
  async pushOutlookEvents(req: OutlookSyncRequest): Promise<OutlookSyncResult> {
    const { data } = await http.post<OutlookSyncResult>("/api/calendar/outlook/sync", req);
    return data;
  },

  // ---- Attachments (supporting documents) ----
  async listAttachments(id: string): Promise<Attachment[]> {
    const { data } = await http.get<Attachment[]>(`/api/recordings/${id}/attachments`);
    return data;
  },
  async addFileAttachment(id: string, file: File): Promise<Attachment> {
    const form = new FormData();
    form.append("file", file, file.name);
    const { data } = await http.post<Attachment>(`/api/recordings/${id}/attachments/file`, form);
    return data;
  },
  async addUrlAttachment(id: string, url: string, name?: string): Promise<Attachment> {
    const { data } = await http.post<Attachment>(`/api/recordings/${id}/attachments/url`, { url, name });
    return data;
  },
  async addMarkdownAttachment(id: string, name: string, content: string): Promise<Attachment> {
    const { data } = await http.post<Attachment>(`/api/recordings/${id}/attachments/markdown`, { name, content });
    return data;
  },
  async renameAttachment(id: string, attachmentId: string, name: string): Promise<void> {
    await http.put(`/api/recordings/${id}/attachments/${attachmentId}`, { name });
  },
  async deleteAttachment(id: string, attachmentId: string): Promise<void> {
    await http.delete(`/api/recordings/${id}/attachments/${attachmentId}`);
  },
  /// Fetch a Markdown attachment's raw text (to seed the in-app editor).
  async getAttachmentText(id: string, attachmentId: string): Promise<string> {
    const { data } = await http.get<string>(`/api/recordings/${id}/attachments/${attachmentId}/content`, {
      responseType: "text",
      transformResponse: [(d) => d],
    });
    return data;
  },
  /// Overwrite a Markdown attachment's content in place (the editor's Save).
  async updateAttachmentContent(id: string, attachmentId: string, content: string): Promise<void> {
    await http.put(`/api/recordings/${id}/attachments/${attachmentId}/content`, { content });
  },
  /// A same-origin, self-authenticating URL the browser can open directly (file attachments only).
  /// Carries the bearer as `access_token` since a new tab can't set an Authorization header.
  attachmentContentUrl(id: string, attachmentId: string): string {
    const token = encodeURIComponent(getToken() ?? "");
    return `${baseURL}/api/recordings/${id}/attachments/${attachmentId}/content?access_token=${token}`;
  },

  /// Self-authenticating URL for the whole-platform backup (Platform Admin only). The browser streams the
  /// `.zip` straight to disk via an anchor href, so nothing is buffered in JS memory.
  backupUrl(): string {
    const token = encodeURIComponent(getToken() ?? "");
    return `${baseURL}/api/maintenance/backup?access_token=${token}`;
  },

  /// Whether the server is assembling a backup archive right now, and how far in. Polled while a download
  /// is in flight so the Maintenance panel can show that the backup is running.
  async backupStatus(): Promise<BackupStatus> {
    const { data } = await http.get<BackupStatus>("/api/maintenance/backup/status");
    return data;
  },

  /// Upload a backup archive to restore the whole platform (destructive). Streams the file as the raw
  /// request body; `onProgress` receives 0–100.
  async restoreBackup(file: File, onProgress?: (percent: number) => void): Promise<RestoreResult> {
    const { data } = await http.post<RestoreResult>("/api/maintenance/restore", file, {
      headers: { "Content-Type": "application/zip" },
      onUploadProgress: (e) => {
        if (onProgress && e.total) onProgress(Math.round((e.loaded / e.total) * 100));
      },
    });
    return data;
  },

  /// Translate the whole transcript (segments + summary + actions) into `language` (BCP-47), or the
  /// caller's native language when omitted. Translations land in each segment's revision.
  async translateRecording(id: string, language?: string): Promise<void> {
    await http.post(`/api/recordings/${id}/translate`, { language: language ?? null });
  },

  async translateSegment(id: string, segmentId: string, language?: string): Promise<void> {
    await http.post(`/api/recordings/${id}/segments/${segmentId}/translate`, { language: language ?? null });
  },

  /// Translate a set of selected segments into `language` (BCP-47; native when omitted) in one batched call.
  async translateSegments(id: string, segmentIds: string[], language?: string): Promise<void> {
    await http.post(`/api/recordings/${id}/segments/translate`, { ids: segmentIds, language: language ?? null });
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

  // ---- Meeting notes (the user's own note lines) ----

  async listNotes(recordingId: string): Promise<MeetingNote[]> {
    const { data } = await http.get<MeetingNote[]>(`/api/recordings/${recordingId}/notes`);
    return data;
  },

  /// Bulk append; used for single adds too. Returns the created lines.
  async createNotes(recordingId: string, lines: { text: string; capturedAtMs?: number | null }[]): Promise<MeetingNote[]> {
    const { data } = await http.post<MeetingNote[]>(`/api/recordings/${recordingId}/notes`, { lines });
    return data;
  },

  async updateNote(recordingId: string, noteId: string, text: string): Promise<void> {
    await http.put(`/api/recordings/${recordingId}/notes/${noteId}`, { text });
  },

  async deleteNote(recordingId: string, noteId: string): Promise<void> {
    await http.delete(`/api/recordings/${recordingId}/notes/${noteId}`);
  },

  /// Pre-meeting notes anchored to a calendar event; adopted onto the recording when the link forms.
  async listEventNotes(calendarId: string, eventId: string): Promise<MeetingNote[]> {
    const { data } = await http.get<MeetingNote[]>(
      `/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes`);
    return data;
  },

  async createEventNotes(calendarId: string, eventId: string, lines: { text: string }[]): Promise<MeetingNote[]> {
    const { data } = await http.post<MeetingNote[]>(
      `/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes`, { lines });
    return data;
  },

  async updateEventNote(calendarId: string, eventId: string, noteId: string, text: string): Promise<void> {
    await http.put(`/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes/${noteId}`, { text });
  },

  async deleteEventNote(calendarId: string, eventId: string, noteId: string): Promise<void> {
    await http.delete(`/api/calendar/events/${encodeURIComponent(calendarId)}/${encodeURIComponent(eventId)}/notes/${noteId}`);
  },

  // ---- Meeting screenshots (captures taken during a recording) ----

  async listScreenshots(recordingId: string): Promise<Screenshot[]> {
    const { data } = await http.get<Screenshot[]>(`/api/recordings/${recordingId}/screenshots`);
    return data;
  },

  /// Upload one capture (full PNG plus JPEG thumbnail) with the clock offset it was taken at.
  async createScreenshot(
    recordingId: string,
    shot: { capturedAtMs: number; width: number; height: number; full: Blob; thumb: Blob },
  ): Promise<Screenshot> {
    const form = new FormData();
    form.append("full", shot.full, "screenshot.png");
    form.append("thumb", shot.thumb, "screenshot.thumb.jpg");
    form.append("capturedAtMs", String(shot.capturedAtMs));
    form.append("width", String(shot.width));
    form.append("height", String(shot.height));
    const { data } = await http.post<Screenshot>(`/api/recordings/${recordingId}/screenshots`, form);
    return data;
  },

  async deleteScreenshot(recordingId: string, screenshotId: string): Promise<void> {
    await http.delete(`/api/recordings/${recordingId}/screenshots/${screenshotId}`);
  },

  /// Self-authenticating image URLs. An <img> request can't set an Authorization header, so the
  /// bearer rides along as `access_token` - the same mechanism attachmentContentUrl uses.
  screenshotContentUrl(recordingId: string, screenshotId: string): string {
    const token = encodeURIComponent(getToken() ?? "");
    return `${baseURL}/api/recordings/${recordingId}/screenshots/${screenshotId}/content?access_token=${token}`;
  },

  screenshotThumbUrl(recordingId: string, screenshotId: string): string {
    const token = encodeURIComponent(getToken() ?? "");
    return `${baseURL}/api/recordings/${recordingId}/screenshots/${screenshotId}/thumb?access_token=${token}`;
  },

  /// Every action across the recordings the user can see (the "Actions" tab). With a roomId it is scoped to
  /// that shared room's recordings; without one, the user's own library.
  async listAllActions(roomId?: string | null): Promise<ActionListItem[]> {
    const { data } = await http.get<ActionListItem[]>(`/api/actions`, { params: roomId ? { roomId } : undefined });
    return data;
  },

  /// The aggregated tag cloud (the "Tags" tab), weight-descending. With a roomId it is scoped to that shared
  /// room's recordings; without one, the user's own library.
  async listTags(roomId?: string | null): Promise<TagCloudEntry[]> {
    const { data } = await http.get<TagCloudEntry[]>("/api/tags", { params: roomId ? { roomId } : undefined });
    return data;
  },

  /// Adopt a tag on a recording - typed by hand, or promoted from a suggestion (same call, pass its text).
  /// Idempotent, so the optimistic UI can retry safely.
  async addRecordingTag(recordingId: string, tag: string): Promise<void> {
    await http.post(`/api/recordings/${recordingId}/tags`, { tag });
  },

  /// Remove an adopted tag. It does not return as a suggestion - only a re-transcription can offer it again.
  async removeRecordingTag(recordingId: string, tag: string): Promise<void> {
    await http.delete(`/api/recordings/${recordingId}/tags`, { params: { tag } });
  },

  /// Reject a suggested tag so it stops being offered on this recording, even after a re-transcription.
  async dismissRecordingTag(recordingId: string, tag: string): Promise<void> {
    await http.post(`/api/recordings/${recordingId}/tags/dismiss`, { tag });
  },

  /// Mark a set of actions complete (or not) in one call — works across recordings. Ids not owned are ignored.
  async completeActions(ids: string[], completed: boolean): Promise<void> {
    await http.post(`/api/actions/complete`, { ids, completed });
  },

  // ---- People directory (and their optional voiceprints) ----

  /// The whole directory. Requires the ManagePeople permission - for a picker, use searchPeople instead,
  /// which is open to everyone precisely so labelling a speaker never needs a permission.
  async listPeople(opts?: {
    q?: string; isInternal?: boolean; hasVoiceprint?: boolean; take?: number; skip?: number;
  }): Promise<Person[]> {
    const { data } = await http.get<Person[]>("/api/people", { params: opts });
    return data;
  },

  /// Find people for a picker. Ungated. Returns nothing below two characters, so a typeahead does not pull
  /// most of the directory on the first keystroke.
  async searchPeople(q: string): Promise<Person[]> {
    const { data } = await http.get<Person[]>("/api/people/search", { params: { q } });
    return data;
  },

  async getPerson(id: string): Promise<PersonDetail> {
    const { data } = await http.get<PersonDetail>(`/api/people/${id}`);
    return data;
  },

  async findPersonDuplicates(): Promise<PersonDuplicateGroup[]> {
    const { data } = await http.get<PersonDuplicateGroup[]>("/api/people/duplicates");
    return data;
  },

  /// Add someone. Passing recordingId + label also enrols a voiceprint from that recording's speaker,
  /// which is what the "new person" control on a transcript does.
  async createPerson(input: {
    name: string; title?: string | null; companyName?: string | null; email?: string | null;
    phone?: string | null; isInternal?: boolean | null; voiceprintOptOut?: boolean | null;
    recordingId?: string | null; label?: string | null;
  }): Promise<Person> {
    const { data } = await http.post<Person>("/api/people", input);
    return data;
  },

  /// Edit a person. Omitted fields are left alone. Setting voiceprintOptOut true erases their voiceprint.
  async updatePerson(id: string, input: {
    name?: string | null; title?: string | null; companyName?: string | null; email?: string | null;
    phone?: string | null; isInternal?: boolean | null; voiceprintOptOut?: boolean | null;
  }): Promise<void> {
    await http.put(`/api/people/${id}`, input);
  },

  async deletePerson(id: string): Promise<void> {
    await http.delete(`/api/people/${id}`);
  },

  async mergePeople(id: string, sourceId: string): Promise<void> {
    await http.post(`/api/people/${id}/merge`, { sourceId });
  },

  async enrolVoiceprint(id: string, recordingId: string, label: string): Promise<void> {
    await http.post(`/api/people/${id}/voiceprint`, { recordingId, label });
  },

  /// Erase the biometric but keep the person.
  async deleteVoiceprint(id: string): Promise<void> {
    await http.delete(`/api/people/${id}/voiceprint`);
  },

  async removeVoiceSample(id: string, sampleId: string): Promise<void> {
    await http.delete(`/api/people/${id}/voiceprint/samples/${sampleId}`);
  },

  /// Reassign a recording's speaker to a person, or pass null to unassign.
  async assignSpeaker(id: string, label: string, personId: string | null): Promise<void> {
    await http.put(`/api/recordings/${id}/speakers/${encodeURIComponent(label)}/assign`, { personId });
  },

  /// GDPR: erase every voiceprint on the platform (auto-labels revert; manual names kept).
  async deleteAllVoiceprints(): Promise<void> {
    await http.delete("/api/people/voiceprints");
  },

  async listSections(roomId?: string | null): Promise<SectionDto[]> {
    const { data } = await http.get<SectionDto[]>("/api/sections", {
      params: roomId ? { roomId } : undefined,
    });
    return data;
  },

  /// Search transcripts and folder names. Scope: `sectionId` limits to that folder and its sub-folders,
  /// `roomId` to one room, and `everywhere` spans every room the caller can see (and wins over both).
  async search(params: {
    q: string;
    roomId?: string | null;
    sectionId?: string | null;
    everywhere?: boolean;
    speaker?: string | null;
    from?: string | null;
    to?: string | null;
    limit?: number;
  }): Promise<SearchResponse> {
    const { data } = await http.get<SearchResponse>("/api/search", {
      // Drop the empties rather than sending `?roomId=null`, which the model binder would reject.
      params: Object.fromEntries(Object.entries(params).filter(([, v]) => v !== null && v !== undefined)),
    });
    return data;
  },

  /// Create a section in the given room (its personal room by default). Pass `parentId` for a sub-section
  /// (one level of nesting). Shared rooms need the ManageContents permission (enforced server-side).
  async createSection(name: string, parentId: string | null = null, roomId?: string | null): Promise<SectionDto> {
    const { data } = await http.post<SectionDto>("/api/sections", { name, parentId, roomId: roomId ?? null });
    return data;
  },

  async renameSection(id: string, name: string): Promise<void> {
    await http.put(`/api/sections/${id}`, { name });
  },

  async deleteSection(id: string): Promise<void> {
    await http.delete(`/api/sections/${id}`);
  },

  /// Set the parent + 0-based order of each listed section in one call (drag-and-drop reorder/reparent).
  async reorderSections(parentId: string | null, orderedIds: string[], roomId?: string | null): Promise<void> {
    await http.put("/api/sections/reorder", { parentId, orderedIds, roomId: roomId ?? null });
  },

  // ---- Folder (section) page ----
  async getSection(id: string): Promise<SectionDetail> {
    const { data } = await http.get<SectionDetail>(`/api/sections/${id}`);
    return data;
  },
  async listSectionActions(id: string): Promise<ActionListItem[]> {
    const { data } = await http.get<ActionListItem[]>(`/api/sections/${id}/actions`);
    return data;
  },
  async listSectionNotes(id: string): Promise<SectionNoteItem[]> {
    const { data } = await http.get<SectionNoteItem[]>(`/api/sections/${id}/notes`);
    return data;
  },
  async listSectionAttachments(id: string): Promise<SectionAttachmentItem[]> {
    const { data } = await http.get<SectionAttachmentItem[]>(`/api/sections/${id}/attachments`);
    return data;
  },

  // ---- Folder-direct attachments (filed against the folder itself, not aggregated from transcripts) ----
  async listFolderAttachments(sectionId: string): Promise<Attachment[]> {
    const { data } = await http.get<Attachment[]>(`/api/sections/${sectionId}/folder-attachments`);
    return data;
  },
  async addFolderFileAttachment(sectionId: string, file: File): Promise<Attachment> {
    const form = new FormData();
    form.append("file", file, file.name);
    const { data } = await http.post<Attachment>(`/api/sections/${sectionId}/folder-attachments/file`, form);
    return data;
  },
  async addFolderUrlAttachment(sectionId: string, url: string, name?: string): Promise<Attachment> {
    const { data } = await http.post<Attachment>(`/api/sections/${sectionId}/folder-attachments/url`, { url, name });
    return data;
  },
  async addFolderMarkdownAttachment(sectionId: string, name: string, content: string): Promise<Attachment> {
    const { data } = await http.post<Attachment>(`/api/sections/${sectionId}/folder-attachments/markdown`, { name, content });
    return data;
  },
  async renameFolderAttachment(sectionId: string, attachmentId: string, name: string): Promise<void> {
    await http.put(`/api/sections/${sectionId}/folder-attachments/${attachmentId}`, { name });
  },
  async deleteFolderAttachment(sectionId: string, attachmentId: string): Promise<void> {
    await http.delete(`/api/sections/${sectionId}/folder-attachments/${attachmentId}`);
  },
  async getFolderAttachmentText(sectionId: string, attachmentId: string): Promise<string> {
    const { data } = await http.get<string>(`/api/sections/${sectionId}/folder-attachments/${attachmentId}/content`, {
      responseType: "text",
      transformResponse: [(d) => d],
    });
    return data;
  },
  async updateFolderAttachmentContent(sectionId: string, attachmentId: string, content: string): Promise<void> {
    await http.put(`/api/sections/${sectionId}/folder-attachments/${attachmentId}/content`, { content });
  },
  /// Self-authenticating URL to open a folder-direct file attachment in a new tab.
  folderAttachmentContentUrl(sectionId: string, attachmentId: string): string {
    const token = encodeURIComponent(getToken() ?? "");
    return `${baseURL}/api/sections/${sectionId}/folder-attachments/${attachmentId}/content?access_token=${token}`;
  },
  /// Kick off (async) generation of the folder summary. Regenerates any missing per-recording summaries first.
  async generateSectionSummary(id: string): Promise<void> {
    await http.post(`/api/sections/${id}/summary/generate`);
  },
  async updateSectionSummary(id: string, text: string): Promise<void> {
    await http.put(`/api/sections/${id}/summary`, { text });
  },
  /// Kick off (async) generation of the folder minutes through the given meeting-type template (null = General).
  async generateSectionMinutes(id: string, meetingTypeId: string | null): Promise<void> {
    await http.post(`/api/sections/${id}/minutes/generate`, { meetingTypeId });
  },
  async updateSectionMinutes(id: string, text: string): Promise<void> {
    await http.put(`/api/sections/${id}/minutes`, { text });
  },

  /// File a recording into a section of the given room (its personal room by default; null section = ungroup).
  async moveRecording(id: string, sectionId: string | null, roomId?: string | null): Promise<void> {
    await http.put(`/api/recordings/${id}/section`, { sectionId, roomId: roomId ?? null });
  },

  /// Set the section + 0-based order of each listed recording in one call (drag-and-drop).
  async reorderRecordings(sectionId: string | null, orderedIds: string[], roomId?: string | null): Promise<void> {
    await http.put("/api/recordings/reorder", { sectionId, orderedIds, roomId: roomId ?? null });
  },

  /// File several recordings into one folder (or ungroup them all with a null sectionId) in a single call -
  /// the paste side of the move clipboard. Unlike reorderRecordings, this appends: the listed recordings land
  /// after whatever is already in the target, in the order given.
  async moveRecordingsBulk(ids: string[], sectionId: string | null, roomId?: string | null): Promise<void> {
    await http.post("/api/recordings/section", { ids, sectionId, roomId: roomId ?? null });
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

  /// Protect (or unprotect) a recording's audio from deletion (auto + manual).
  async setAudioProtection(id: string, protectedFlag: boolean): Promise<void> {
    await http.put(`/api/recordings/${id}/audio-protection`, { protected: protectedFlag });
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

  /// The user's Google calendars for the Preferences picker (empty when Calendar isn't connected).
  async listCalendars(): Promise<GoogleCalendarListItem[]> {
    const { data } = await http.get<GoogleCalendarListItem[]>("/api/calendar/calendars");
    return data;
  },

  /// Save which Google calendars to consider for recording attribution + the Calendar overlay.
  async saveCalendarSelection(ids: string[]): Promise<void> {
    await http.put("/api/calendar/calendars", { ids });
  },

  async getUserSettings(): Promise<UserSettings> {
    const { data } = await http.get<UserSettings>("/api/user/settings");
    return data;
  },

  async updateUserSettings(body: UpdateUserSettings): Promise<void> {
    await http.put("/api/user/settings", body);
  },

  // ---- MCP access tokens (Claude connection) ----

  async listMcpTokens(): Promise<McpToken[]> {
    const { data } = await http.get<McpToken[]>("/api/user/mcp-tokens");
    return data;
  },

  /// Generate a new MCP token. The plaintext token is returned once — the caller must show it to the user
  /// immediately (it can never be retrieved again).
  async createMcpToken(name: string): Promise<McpTokenCreated> {
    const { data } = await http.post<McpTokenCreated>("/api/user/mcp-tokens", { name });
    return data;
  },

  async revokeMcpToken(id: string): Promise<void> {
    await http.delete(`/api/user/mcp-tokens/${id}`);
  },

  // ---- Personal REST-API tokens (Developers / API access) ----

  async listApiTokens(): Promise<ApiToken[]> {
    const { data } = await http.get<ApiToken[]>("/api/user/api-tokens");
    return data;
  },

  /// Generate a new API token. The plaintext token is returned once - show it to the user immediately.
  async createApiToken(name: string, options?: CreateApiTokenOptions): Promise<ApiTokenCreated> {
    const { data } = await http.post<ApiTokenCreated>("/api/user/api-tokens", {
      name,
      readOnly: options?.readOnly ?? false,
      expiresAt: options?.expiresAt ?? null,
    });
    return data;
  },

  async revokeApiToken(id: string): Promise<void> {
    await http.delete(`/api/user/api-tokens/${id}`);
  },

  /// The curated OpenAPI document (authed) that backs the in-app API reference at /developers/api.
  async getOpenApiDocument(): Promise<unknown> {
    const { data } = await http.get("/api/openapi/v1.json");
    return data;
  },

  /// Active OAuth connections (the claude.ai web MCP connector and any other OAuth client the user approved).
  async listOAuthConnections(): Promise<OAuthConnection[]> {
    const { data } = await http.get<OAuthConnection[]>("/api/oauth/connections");
    return data;
  },

  /// Revoke an OAuth connection - deletes the authorization + its tokens so the client can no longer connect.
  async revokeOAuthConnection(id: string): Promise<void> {
    await http.delete(`/api/oauth/connections/${encodeURIComponent(id)}`);
  },

  // ---- Outbound webhooks (Automations) ----

  async listWebhooks(): Promise<WebhookSubscription[]> {
    const { data } = await http.get<WebhookSubscription[]>("/api/user/webhooks");
    return data;
  },

  /// Create a webhook. The response's plaintext `secret` is returned once - show it to the user immediately.
  async createWebhook(body: CreateWebhookBody): Promise<WebhookCreated> {
    const { data } = await http.post<WebhookCreated>("/api/user/webhooks", body);
    return data;
  },

  async updateWebhook(id: string, body: UpdateWebhookBody): Promise<WebhookSubscription> {
    const { data } = await http.put<WebhookSubscription>(`/api/user/webhooks/${id}`, body);
    return data;
  },

  async deleteWebhook(id: string): Promise<void> {
    await http.delete(`/api/user/webhooks/${id}`);
  },

  async testWebhook(id: string): Promise<void> {
    await http.post(`/api/user/webhooks/${id}/test`);
  },

  async listWebhookDeliveries(id: string): Promise<WebhookDelivery[]> {
    const { data } = await http.get<WebhookDelivery[]>(`/api/user/webhooks/${id}/deliveries`);
    return data;
  },

  // ---- Platform webhooks (admin-managed) ----

  async listPlatformWebhooks(): Promise<WebhookSubscription[]> {
    const { data } = await http.get<WebhookSubscription[]>("/api/admin/webhooks");
    return data;
  },

  /// Create a platform webhook. The response's plaintext `secret` is returned once - show it to the user immediately.
  async createPlatformWebhook(body: CreatePlatformWebhookBody): Promise<WebhookCreated> {
    const { data } = await http.post<WebhookCreated>("/api/admin/webhooks", body);
    return data;
  },

  async updatePlatformWebhook(
    id: string,
    body: CreatePlatformWebhookBody & { isActive: boolean },
  ): Promise<WebhookSubscription> {
    const { data } = await http.put<WebhookSubscription>(`/api/admin/webhooks/${id}`, body);
    return data;
  },

  async deletePlatformWebhook(id: string): Promise<void> {
    await http.delete(`/api/admin/webhooks/${id}`);
  },

  // ---- Workflow signals ----

  async listWorkflowSignals(): Promise<WorkflowSignal[]> {
    const { data } = await http.get<WorkflowSignal[]>("/api/workflow-signals");
    return data;
  },

  /// Every workflow signal, including inactive ones, for the admin manage popup.
  async listAllWorkflowSignals(): Promise<WorkflowSignal[]> {
    const { data } = await http.get<WorkflowSignal[]>("/api/workflow-signals/manage");
    return data;
  },

  async createWorkflowSignal(body: CreateWorkflowSignalBody): Promise<WorkflowSignal> {
    const { data } = await http.post<WorkflowSignal>("/api/workflow-signals", body);
    return data;
  },

  async updateWorkflowSignal(id: string, body: UpdateWorkflowSignalBody): Promise<WorkflowSignal> {
    const { data } = await http.put<WorkflowSignal>(`/api/workflow-signals/${id}`, body);
    return data;
  },

  async deleteWorkflowSignal(id: string): Promise<void> {
    await http.delete(`/api/workflow-signals/${id}`);
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

  /// Run the audio-retention deletion pass immediately (Platform Administrator). Returns how many
  /// recordings had their audio deleted.
  async runAudioRetention(): Promise<{ deleted: number }> {
    const { data } = await http.post<{ deleted: number }>("/api/platform/settings/run-audio-retention");
    return data;
  },

  /// Backfill tag-cloud tags immediately (Platform Administrator): queues an extraction job for every
  /// never-tagged recording. Returns how many jobs were queued (they complete asynchronously).
  async runTagBackfill(): Promise<{ enqueued: number }> {
    const { data } = await http.post<{ enqueued: number }>("/api/platform/settings/run-tag-backfill");
    return data;
  },

  // ---- Chat ----

  /// The models this user may choose between for chat. Never carries an endpoint or a key - unlike
  /// `listModels`, which is administrator-only for exactly that reason.
  async listChatModels(): Promise<ChatModelOption[]> {
    const { data } = await http.get<ChatModelOption[]>("/api/chat/models");
    return data;
  },

  /// Stream a chat reply. Uses raw fetch (not the axios instance) so the response body can be read
  /// incrementally; the JWT bearer must therefore be attached manually here. Resolves with the final
  /// context-usage snapshot.
  async chatStream(
    body: {
      recordingIds: string[];
      sectionId?: string | null;
      attachmentName?: string | null;
      attachmentText?: string | null;
      messages: ChatTurn[];
      includeAttachments?: boolean;
      searchAllMeetings?: boolean;
      /// Which model answers this turn (an id from `listChatModels`). Omit or null for the platform's
      /// chat model. Chat is stateless per turn, so changing it mid-conversation needs nothing else - the
      /// full history is resent and reaches the new model with the request.
      modelId?: string | null;
    },
    handlers: {
      onToken: (token: string) => void;
      onMeta?: (u: ChatUsage) => void;
      onToolStart?: (name: string) => void;
      onToolEnd?: (name: string) => void;
      onRef?: (name: string, href: string) => void;
      onAttachmentDraft?: (draft: AttachmentDraft) => void;
      signal?: AbortSignal;
    },
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
          name?: string;
          href?: string;
          content?: string;
          recordings?: { id: string; title: string }[];
          model?: string;
          message?: string;
          contextUsed?: number;
          contextTotal?: number;
        };
        if (evt.type === "token" && evt.value) {
          handlers.onToken(evt.value);
        } else if (evt.type === "tool_start" && evt.name) {
          handlers.onToolStart?.(evt.name);
        } else if (evt.type === "tool_end" && evt.name) {
          handlers.onToolEnd?.(evt.name);
        } else if (evt.type === "ref" && evt.name && evt.href) {
          handlers.onRef?.(evt.name, evt.href);
        } else if (evt.type === "attachment" && evt.name && evt.content !== undefined && evt.recordings) {
          handlers.onAttachmentDraft?.({ name: evt.name, content: evt.content, recordings: evt.recordings });
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

  async transcribeChat(blob: Blob): Promise<string> {
    const form = new FormData();
    form.append("file", blob, "utterance.webm");
    const { data } = await http.post<{ text: string }>("/api/chat/transcribe", form);
    return data.text;
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

  /// Re-transcribe. `speakers` and `language` are both tri-state: omit one to keep whatever the recording
  /// already has, or pass an object to set it (null bounds = automatic diarization; a null `code` = let
  /// Whisper detect the language again).
  async retranscribe(
    id: string,
    opts?: {
      model?: string | null;
      speakers?: { min: number | null; max: number | null };
      language?: { code: string | null };
    },
  ): Promise<void> {
    await http.post(`/api/recordings/${id}/retranscribe`, {
      model: opts?.model ?? null,
      speakers: opts?.speakers ?? null,
      language: opts?.language ?? null,
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

  // ---- Formulas ----

  /// The caller's own Personal formulas plus every enabled Platform/Diariz formula.
  async listFormulas(): Promise<Formula[]> {
    const { data } = await http.get<Formula[]>("/api/formulas");
    return data;
  },

  /// ManageFormulas-gated: every Platform + Diariz formula, including disabled ones, for the admin popup.
  async listManagedFormulas(): Promise<Formula[]> {
    const { data } = await http.get<Formula[]>("/api/formulas/managed");
    return data;
  },

  async createFormula(body: {
    scope: FormulaScope;
    name: string;
    description?: string | null;
    content: TemplateContent;
    context: number;
    shared?: boolean;
    signals?: string[];
  }): Promise<Formula> {
    const { data } = await http.post<Formula>("/api/formulas", body);
    return data;
  },

  /// Partial update: omitted fields are left unchanged.
  async updateFormula(
    id: string,
    body: {
      name?: string;
      description?: string | null;
      content?: TemplateContent;
      context?: number;
      shared?: boolean;
      signals?: string[];
    },
  ): Promise<Formula> {
    const { data } = await http.put<Formula>(`/api/formulas/${id}`, body);
    return data;
  },

  async deleteFormula(id: string): Promise<void> {
    await http.delete(`/api/formulas/${id}`);
  },

  /// Formulas shared by other users, for the discovery browser.
  async listSharedFormulas(): Promise<SharedFormula[]> {
    const { data } = await http.get<SharedFormula[]>("/api/formulas/shared");
    return data;
  },

  /// Add a shared formula to the caller's collection (idempotent).
  async subscribeFormula(id: string): Promise<void> {
    await http.post(`/api/formulas/${id}/subscribe`);
  },

  /// Remove the caller's link to a shared formula (idempotent).
  async unsubscribeFormula(id: string): Promise<void> {
    await http.delete(`/api/formulas/${id}/subscribe`);
  },

  /// Enable/disable a Platform/Diariz formula (Personal formulas are always available).
  async setFormulaEnabled(id: string, enabled: boolean): Promise<void> {
    await http.put(`/api/formulas/${id}/enabled`, { enabled });
  },

  /// Run a formula over a recording and return the persisted result.
  async runFormula(recordingId: string, formulaId: string): Promise<FormulaResult> {
    const { data } = await http.post<FormulaResult>(`/api/recordings/${recordingId}/formulas/${formulaId}/run`);
    return data;
  },

  // ---- Formula results (per recording) ----

  async listFormulaResults(recordingId: string): Promise<FormulaResult[]> {
    const { data } = await http.get<FormulaResult[]>(`/api/recordings/${recordingId}/formula-results`);
    return data;
  },

  /// Fetch a formula result's generated Markdown body (fetched separately so listing stays cheap).
  async getFormulaResultText(recordingId: string, id: string): Promise<string> {
    const { data } = await http.get<{ text: string }>(`/api/recordings/${recordingId}/formula-results/${id}`);
    return data.text;
  },

  async updateFormulaResult(recordingId: string, id: string, text: string): Promise<FormulaResult> {
    const { data } = await http.put<FormulaResult>(`/api/recordings/${recordingId}/formula-results/${id}`, { text });
    return data;
  },

  async deleteFormulaResult(recordingId: string, id: string): Promise<void> {
    await http.delete(`/api/recordings/${recordingId}/formula-results/${id}`);
  },

  /// Email a formula result's Markdown to the signed-in user's own registered address.
  async emailFormulaResult(recordingId: string, id: string): Promise<void> {
    await http.post(`/api/recordings/${recordingId}/formula-results/${id}/email`);
  },

  async downloadFormulaResult(recordingId: string, id: string): Promise<void> {
    const res = await http.get(`/api/recordings/${recordingId}/formula-results/${id}/download`, {
      responseType: "blob",
    });
    triggerBlobDownload(res.data as Blob, filenameFromHeaders(res.headers, "formula-result.md"));
  },

  // ---- Section (folder) formulas + results ----
  // Mirror the recording endpoints against /api/sections/{id}/... - the worker/callback contract is the same,
  // only the target (a folder) differs.

  /// Run a formula over a section (folder) and return the persisted result.
  async runSectionFormula(sectionId: string, formulaId: string): Promise<SectionFormulaResult> {
    const { data } = await http.post<SectionFormulaResult>(`/api/sections/${sectionId}/formulas/${formulaId}/run`);
    return data;
  },

  async listSectionFormulaResults(sectionId: string): Promise<SectionFormulaResult[]> {
    const { data } = await http.get<SectionFormulaResult[]>(`/api/sections/${sectionId}/formula-results`);
    return data;
  },

  /// Fetch a section formula result's generated Markdown body (fetched separately so listing stays cheap).
  async getSectionFormulaResultText(sectionId: string, id: string): Promise<string> {
    const { data } = await http.get<{ text: string }>(`/api/sections/${sectionId}/formula-results/${id}`);
    return data.text;
  },

  async updateSectionFormulaResult(sectionId: string, id: string, text: string): Promise<SectionFormulaResult> {
    const { data } = await http.put<SectionFormulaResult>(`/api/sections/${sectionId}/formula-results/${id}`, { text });
    return data;
  },

  async deleteSectionFormulaResult(sectionId: string, id: string): Promise<void> {
    await http.delete(`/api/sections/${sectionId}/formula-results/${id}`);
  },

  /// Email a section formula result's Markdown to the signed-in user's own registered address.
  async emailSectionFormulaResult(sectionId: string, id: string): Promise<void> {
    await http.post(`/api/sections/${sectionId}/formula-results/${id}/email`);
  },

  async downloadSectionFormulaResult(sectionId: string, id: string): Promise<void> {
    const res = await http.get(`/api/sections/${sectionId}/formula-results/${id}/download`, {
      responseType: "blob",
    });
    triggerBlobDownload(res.data as Blob, filenameFromHeaders(res.headers, "formula-result.md"));
  },

  /// Submit a user-reported problem, alongside the scrubbed trail and the app version - the release is
  /// added here (rather than by the caller) so every submission is tagged with the build that produced it.
  async submitFeedback(description: string, route: string, trailJson: string): Promise<{ id: string }> {
    const { data } = await http.post<{ id: string }>("/api/feedback", {
      description, route, release: __APP_VERSION__, trailJson,
    });
    return data;
  },

  /// Platform Administrator only. Newest first (the server already orders it; this is what `GET /api/feedback`
  /// returns raw - no client-side reshaping needed).
  async listFeedback(): Promise<FeedbackDto[]> {
    const { data } = await http.get<FeedbackDto[]>("/api/feedback");
    return data;
  },

  async deleteFeedback(id: string): Promise<void> {
    await http.delete(`/api/feedback/${id}`);
  },

  // ---- LLM usage log (Platform Administrator) ----
  // Array filter fields (userIds/kinds/models) must serialize as repeated keys ("userIds=a&userIds=b"),
  // not axios's default "userIds[]=a&userIds[]=b" - ASP.NET Core's [FromQuery] array binder does not
  // recognise the bracketed form. `paramsSerializer: { indexes: null }` selects the repeated-key form.

  /// List LlmCalls entries. mode=operations (default) collapses every call in one operation into a
  /// single row; mode=calls returns one row per call - narrow the returned rows by the `mode` you passed.
  /// `total`/`totals` both cover the whole filtered set, never just the returned page.
  async getLlmUsage(params: LlmUsageFilter & {
    mode?: "operations" | "calls";
    sort?: LlmUsageSortKey;
    desc?: boolean;
    page?: number;
    pageSize?: number;
  }): Promise<LlmUsagePage<LlmUsageOperationRow | LlmUsageCallRow>> {
    const { data } = await http.get<LlmUsagePage<LlmUsageOperationRow | LlmUsageCallRow>>(
      "/api/admin/llm-usage",
      { params, paramsSerializer: { indexes: null } },
    );
    return data;
  },

  /// Roll usage up by whichever of user/model/kind `groupBy` names (sent as a comma-separated list).
  async getLlmUsageSummary(params: LlmUsageFilter & { groupBy: LlmUsageGroupDimension[] }): Promise<LlmUsageSummary> {
    const { groupBy, ...filter } = params;
    const { data } = await http.get<LlmUsageSummary>("/api/admin/llm-usage/summary", {
      params: { ...filter, groupBy: groupBy.join(",") },
      paramsSerializer: { indexes: null },
    });
    return data;
  },

  /// Permanently delete every LlmCalls row matching the filter. No undo - the usage log is the only
  /// record a given LLM call ever happened. Returns how many rows were removed.
  async deleteLlmUsage(params: LlmUsageFilter): Promise<{ deleted: number }> {
    const { data } = await http.delete<{ deleted: number }>("/api/admin/llm-usage", {
      params,
      paramsSerializer: { indexes: null },
    });
    return data;
  },

  /// The distinct users, models and kinds present in the usage log within the given date window (default:
  /// the same 30-day window every other endpoint here defaults to), for populating the viewer's filters.
  async getLlmUsageFilters(params?: { from?: string | null; to?: string | null }): Promise<LlmUsageFilterOptions> {
    const { data } = await http.get<LlmUsageFilterOptions>("/api/admin/llm-usage/filters", { params });
    return data;
  },

  // ---- Platform LLM models ----

  async listModels(): Promise<LlmModel[]> {
    const { data } = await http.get<LlmModel[]>("/api/admin/llm-models");
    return data;
  },

  async createModel(model: LlmModelUpsert): Promise<LlmModel> {
    const { data } = await http.post<LlmModel>("/api/admin/llm-models", model);
    return data;
  },

  /// Omit `apiKey` to keep the stored key, send "" to clear it, send a value to replace it. The client is
  /// never given the key, so it cannot echo one back.
  async updateModel(id: string, model: LlmModelUpsert): Promise<LlmModel> {
    const { data } = await http.put<LlmModel>(`/api/admin/llm-models/${id}`, model);
    return data;
  },

  /// Whether a model appears in the chat model picker. Its own route rather than a field on updateModel,
  /// so a save from the editor drawer cannot post a stale value and silently un-offer the model.
  async setModelChatEnabled(id: string, enabled: boolean): Promise<void> {
    await http.put(`/api/admin/llm-models/${id}/chat-enabled`, { enabled });
  },

  /// 409 while any call group or the platform default still points at the model.
  async deleteModel(id: string): Promise<void> {
    await http.delete(`/api/admin/llm-models/${id}`);
  },

  /// The application defaults every unset parameter resolves through, keyed by call group. Static for the
  /// lifetime of the server process - they come from configuration, not the database.
  async getLlmModelDefaults(): Promise<Record<string, string>> {
    const { data } = await http.get<Record<string, string>>("/api/admin/llm-models/defaults");
    return data;
  },

  /// Run one sample call against a model with the parameters currently on screen, saved or not. The
  /// endpoint, key and model name come from the stored row - this never sends them.
  async testModel(
    id: string,
    body: { group: string; parameters: Record<string, string> },
  ): Promise<LlmTestOutcome> {
    const { data } = await http.post<LlmTestOutcome>(`/api/admin/llm-models/${id}/test`, body);
    return data;
  },

  async getLlmAssignments(): Promise<LlmAssignments> {
    const { data } = await http.get<LlmAssignments>("/api/admin/llm-models/assignments");
    return data;
  },

  async setLlmAssignments(assignments: LlmAssignments): Promise<void> {
    await http.put("/api/admin/llm-models/assignments", assignments);
  },

  /// One-time migration aid: creates the first model from the endpoint already in the environment.
  /// 409 once any model exists.
  async createModelFromEnvironment(): Promise<LlmModel> {
    const { data } = await http.post<LlmModel>("/api/admin/llm-models/from-environment");
    return data;
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
//
// Not every error comes from the API: a reverse proxy in front of it answers refusals of its own (413, 502,
// 504) with an HTML error page, and nginx pads that page with several hundred bytes of "a padding to disable
// MSIE and Chrome friendly error page" comments. Rendering that verbatim buried the actual problem, so a
// markup body is treated as no message at all and the status code speaks instead.
export function apiErrorMessage(e: unknown, fallback = "Something went wrong."): string {
  if (axios.isAxiosError(e)) {
    if (!e.response) return "Cannot reach the server.";
    const status = e.response.status;
    const data = e.response.data as any;
    if (typeof data === "string" && data.trim() && !isMarkup(data)) return data;
    if (status === 413) return "The file is too large for the server to accept.";
    if (Array.isArray(data) && data.length) return data.map(String).join(" ");
    if (data?.errors) {
      const msgs = Object.values(data.errors).flat();
      if (msgs.length) return msgs.map(String).join(" ");
    }
    if (data?.title) return data.title;
    return `Request failed (${status}).`;
  }
  return e instanceof Error ? e.message : fallback;
}

/// True for a body that is an HTML/XML document rather than a message meant to be read - the shape a proxy's
/// own error page arrives in.
function isMarkup(body: string): boolean {
  return body.trimStart().startsWith("<");
}
