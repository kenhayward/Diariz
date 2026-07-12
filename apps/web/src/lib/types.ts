// Mirrors the .NET API DTOs (src/Diariz.Api/Contracts/ApiDtos.cs).

export type RecordingStatus =
  | "Uploaded"
  | "Queued"
  | "Transcribing"
  | "Transcribed"
  | "Summarizing"
  | "Summarized"
  | "Merging"
  | "Failed";

export type RecordingSource = "Microphone" | "System" | "Upload" | "Combined";

export interface RecordingSummary {
  id: string;
  title: string;
  name: string | null;
  source: RecordingSource;
  durationMs: number;
  status: RecordingStatus;
  createdAt: string;
  sectionId: string | null;
  sectionName: string | null;
  /// Whether the recording already has extracted action items (drives the list's re-extract confirm).
  hasActions: boolean;
  /// Whether the original audio is still present (false once the audio has been deleted).
  hasAudio: boolean;
  /// The linked Google Calendar event id, or null when unlinked. Drives the list's calendar icon and lets
  /// the Calendar tab dedupe a recording against its own event.
  calendarEventId: string | null;
  /// The linked calendar's Google colour (hex) for tinting the list's calendar icon. Null when unlinked.
  calendarColor?: string | null;
  /// The chosen meeting type driving the minutes template, or null for the General default.
  meetingTypeId?: string | null;
}

// ---- Tag cloud ----
/// One aggregated tag across the caller's library (GET /api/tags): display text, how many recordings carry
/// it, the summed per-recording weight (drives the cloud's font size), and the carrying recording ids (so
/// the client filters its cached recordings list without a second request).
export interface TagCloudEntry {
  tag: string;
  count: number;
  weight: number;
  recordingIds: string[];
}

// ---- Meeting types (minutes templates) ----
/// A block within a template section: literal boilerplate text, a substituted recording field, a model prompt, or a
/// horizontal rule (`hr`, which carries no text or field).
export type TemplateBlockKind = "boilerplate" | "field" | "prompt" | "hr";
export interface TemplateBlock {
  kind: TemplateBlockKind;
  text?: string | null;
  field?: string | null;
  /// The break emitted after this block (see MeetingTypeMinutesComposer). Absent = legacy rule.
  breakAfter?: "none" | "line" | "paragraph" | null;
}
/// One H1/H2 section (level 1 or 2) of a template, with its ordered content blocks.
export interface TemplateSection {
  level: number;
  title: string;
  blocks: TemplateBlock[];
}
export interface MeetingTypeContent {
  sections: TemplateSection[];
}
/// A meeting type (minutes template). `isPlatform` = a shared, admin-owned type; `canEdit` = the caller may
/// edit/delete it (owns a Personal type, or is a Platform Admin for a Platform type).
export interface MeetingType {
  id: string;
  isPlatform: boolean;
  canEdit: boolean;
  groupName: string;
  title: string;
  overview: string;
  icon: string;
  color: string;
  content: MeetingTypeContent;
  /// True for the seeded "General Meeting" default (used when a recording has no explicit type).
  isDefault: boolean;
}

/// Create/update payload for a meeting type. `isPlatform` is honoured only for Platform Administrators.
export interface MeetingTypeInput {
  groupName: string;
  title: string;
  overview: string;
  icon: string;
  color: string;
  content: MeetingTypeContent;
  isPlatform: boolean;
}

export interface SectionDto {
  id: string;
  name: string;
  /// Null for a top-level section; the parent section's id for a sub-section (one level of nesting).
  parentId: string | null;
  /// Manual sort order among siblings.
  position: number;
}

// ---- Folder (section) page ----
/// Generation lifecycle of a folder-level LLM artifact (summary/minutes).
export type SectionGenerationStatus = "Idle" | "Generating" | "Ready" | "Failed";

export interface SectionStats {
  transcriptCount: number;
  totalDurationMs: number;
  firstRecordingAt: string | null;
  lastRecordingAt: string | null;
}
export interface FolderSummary {
  model: string;
  text: string;
  createdAt: string;
  isUserEdited: boolean;
  status: SectionGenerationStatus;
  error: string | null;
}
export interface FolderMinutes {
  model: string;
  text: string;
  createdAt: string;
  isUserEdited: boolean;
  meetingTypeId: string | null;
  status: SectionGenerationStatus;
  error: string | null;
}
export interface SectionDetail {
  id: string;
  name: string;
  parentId: string | null;
  stats: SectionStats;
  summary: FolderSummary | null;
  minutes: FolderMinutes | null;
  meetingTypeId: string | null;
}
/// One note aggregated for the folder Notes tab (carries its source recording's display name).
export interface SectionNoteItem {
  id: string;
  recordingId: string;
  recordingName: string;
  text: string;
  capturedAtMs: number | null;
  ordinal: number;
  createdAt: string;
}
/// One attachment aggregated for the folder Attachments tab (carries its source recording's display name).
export interface SectionAttachmentItem {
  id: string;
  recordingId: string;
  recordingName: string;
  kind: AttachmentKind;
  name: string;
  contentType: string | null;
  sizeBytes: number;
  url: string | null;
  ordinal: number;
}

/// A supporting document attached to a recording — an uploaded file or a URL.
export type AttachmentKind = "File" | "Url";
export interface Attachment {
  id: string;
  kind: AttachmentKind;
  name: string;
  contentType: string | null;
  sizeBytes: number;
  url: string | null;
  ordinal: number;
}

export interface SummaryDto {
  model: string;
  text: string;
  createdAt: string;
  /// The user hand-wrote/edited this summary; the auto-summariser won't overwrite it without a warning.
  isUserEdited: boolean;
}

export interface MeetingMinutesDto {
  model: string;
  /// GitHub-flavoured Markdown (headings, lists, tables, bold).
  text: string;
  createdAt: string;
  /// The user hand-edited these minutes; the auto-generator won't overwrite them without a warning.
  isUserEdited: boolean;
}

export interface SegmentDto {
  id: string;
  speaker: string;
  speakerDisplay: string;
  startMs: number;
  endMs: number;
  /** The model's verbatim output for this span. */
  original: string;
  /** A user edit or translation; null/undefined = unchanged (show the original). */
  revised: string | null;
  /** The text shown/exported: the revision when present, else the original. */
  text: string;
}

export interface TranscriptionDto {
  id: string;
  model: string;
  version: number;
  language: string | null;
  createdAt: string;
  segments: SegmentDto[];
  /// Full-pipeline wall-clock time the worker spent producing this transcription (ms); null if untracked.
  processingMs: number | null;
}

/// A diarized speaker in a recording: its label, shown name, the enrolled voiceprint it's
/// linked to (if any), and whether the name was applied automatically by identification.
export interface SpeakerInfo {
  label: string;
  displayName: string;
  profileId: string | null;
  identifiedAuto: boolean;
  /// The user has marked this slot as overlapping/simultaneous speech ("Multiple Speakers").
  /// Such a speaker is never auto-identified or enrolled into a voiceprint.
  isMultiSpeaker: boolean;
}

/// An enrolled person/voiceprint (per user).
export interface SpeakerProfile {
  id: string;
  name: string;
  sampleCount: number;
}

/// One training sample feeding a voiceprint (the recording-speaker it came from).
export interface SpeakerProfileContribution {
  id: string;
  recordingId: string;
  recordingName: string;
  speakerLabel: string;
  /// Start (ms) of that speaker's first segment, so the UI can play a sample of the voice.
  startMs: number;
  createdAt: string;
}

/// A voiceprint with its training provenance and how many recording-speakers it currently labels.
export interface SpeakerProfileDetail {
  id: string;
  name: string;
  sampleCount: number;
  identifiedCount: number;
  contributions: SpeakerProfileContribution[];
}

/// An action item extracted from (or hand-added to) a transcript. All fields are free text; `text` is
/// the action itself (shown as the "Action" column); `actor`/`deadline` may be empty. `completed` is a
/// user-set done flag (reversible); `completedAt` is the ISO timestamp it was marked done (null = not done).
export interface RecordingAction {
  id: string;
  text: string;
  actor: string;
  deadline: string;
  ordinal: number;
  completed: boolean;
  completedAt: string | null;
}

/// One line of the user's own meeting notes. capturedAtMs = offset into the recording clock
/// (null = pre-meeting/post-hoc); immutable after capture.
export interface MeetingNote {
  id: string;
  text: string;
  capturedAtMs: number | null;
  ordinal: number;
  createdAt: string;
}

/// An action across the whole library (the "Actions" tab), carrying its source recording so the row can
/// link back to that transcript.
export interface ActionListItem {
  id: string;
  recordingId: string;
  recordingName: string;
  text: string;
  actor: string;
  deadline: string;
  ordinal: number;
  completed: boolean;
  completedAt: string | null;
  createdAt: string;
}

export interface RecordingDetail {
  id: string;
  title: string;
  name: string | null;
  source: RecordingSource;
  durationMs: number;
  sizeBytes: number;
  status: RecordingStatus;
  error: string | null;
  createdAt: string;
  /// Optional pyannote diarization hints (null = automatic).
  minSpeakers: number | null;
  maxSpeakers: number | null;
  speakerNames: Record<string, string>;
  speakers: SpeakerInfo[];
  current: TranscriptionDto | null;
  summary: SummaryDto | null;
  meetingMinutes: MeetingMinutesDto | null;
  /// Extracted action items (only meaningful once `actionsExtracted` is true).
  actions: RecordingAction[];
  /// Whether action extraction has been run — drives the "show the Actions panel by exception" rule.
  actionsExtracted: boolean;
  /// Whether the original audio is still present (false once the audio has been deleted).
  hasAudio: boolean;
  /// When the owner protected the audio from deletion (null = not protected).
  audioProtectedAt: string | null;
  /// When the audio blob was deleted (null = still present). Mirrors `hasAudio`.
  audioDeletedAt: string | null;
  /// Projected date the nightly job will delete this recording's audio, or null when auto-delete is off,
  /// the recording is protected/ineligible, or the audio is already gone.
  audioScheduledDeletionAt: string | null;
  /// The persisted Google Calendar link (snapshot), or null when unlinked.
  calendarLink: CalendarLink | null;
  /// The chosen meeting type driving the minutes template, or null for the General default.
  meetingTypeId?: string | null;
  /// Who recorded it (the owner), and their display name (null = a deleted/unknown user).
  recordedByUserId: string | null;
  recordedByName: string | null;
  /// The rooms this recording is placed in that the caller can see, home (main) room first.
  rooms: RecordingRoom[] | null;
}

/// A room a recording sits in, for the detail Overview. `isMain` = the recorder's personal (home) room -
/// the only room it can be deleted from.
export interface RecordingRoom {
  id: string;
  name: string;
  icon: string | null;
  color: string | null;
  isMain: boolean;
}

export interface AuthResponse {
  accessToken: string;
  expiresAt: string;
}

// ---- Users / access requests ----
export type UserAccountStatus = "Requested" | "Invited" | "Active";

export interface AdminUser {
  id: string;
  email: string;
  fullName: string | null;
  accountType: "Standard" | "Administrator" | "PlatformAdministrator";
  status: UserAccountStatus;
  isEnabled: boolean;
  quotaBytes: number;
  usedBytes: number;
  hasGoogle: boolean; // account is linked to a Google identity
}

/// The signed-in user's storage usage vs quota (bytes), plus total transcription wall-clock time (ms).
export interface UserStorage {
  usedBytes: number;
  quotaBytes: number;
  totalTranscriptionMs: number;
}

/// Platform-wide storage-quota defaults (bytes), edited by the Platform Administrator.
/// How template-driven minutes generate (platform-wide, admin-only). The API serialises enums by name
/// (JsonStringEnumConverter), so this is the string name on the wire - not a number.
export type MinutesGenerationMode = "SingleCall" | "PerSection";
export interface PlatformSettings {
  starterQuotaBytes: number;
  maxQuotaBytes: number;
  minutesGenerationMode: MinutesGenerationMode;
  /// Audio retention: master switch (off by default), the retention window in days, and the
  /// server-local time of day the nightly deletion job runs (serialised as "HH:mm:ss").
  autoDeleteAudioEnabled: boolean;
  audioRetentionDays: number;
  audioDeletionTimeOfDay: string;
  /// Master switch for user API access (personal tokens). Off by default.
  apiAccessEnabled: boolean;
  /// Per-request timeout (seconds) applied to every LLM call platform-wide. Default 120.
  llmTimeoutSeconds: number;
}

export interface GrantResult {
  emailed: boolean;
  setupUrl: string | null;
}

export interface SetupValidation {
  valid: boolean;
  email: string | null;
  fullName: string | null;
}

export interface UserSettings {
  apiBase: string | null;
  model: string | null;
  hasApiKey: boolean;
  /// Server-wide defaults, shown as placeholders (applied when the user leaves a field blank).
  defaultApiBase: string | null;
  defaultModel: string | null;
  serverHasApiKey: boolean;
  /// Per-user chat context-window override (tokens); null = use the server default.
  contextWindow: number | null;
  defaultContextWindow: number;
  /// Effective master switch for chat tool calling (user override ?? server default).
  toolsEnabled: boolean;
  defaultToolsEnabled: boolean;
  /// The catalog of built-in chat tools with their resolved on/off state.
  tools: ChatToolInfo[];
  /// Effective reasoning toggle + level (user override ?? server default) for reasoning models.
  reasoningEnabled: boolean;
  reasoningEffort: string; // "low" | "medium" | "high"
  defaultReasoningEnabled: boolean;
  defaultReasoningEffort: string;
  /// Where a new recording lands in the user's Personal room (enum name on the wire).
  placementMode: RecordingPlacementMode;
  placementSectionId: string | null;
  /// True when the server has an STT endpoint configured (dictation server-fallback path is available).
  dictationServerAvailable: boolean;
}

/// Where a new recording lands in the user's Personal room. Mirrors the server enum names.
export type RecordingPlacementMode = "Ungrouped" | "SelectedFolder" | "SpecificFolder";

/// The outcome of a platform restore. When the backup was from an older (forward-compatible) schema, its
/// data is migrated up to the current version - `migratedFrom` !== `migratedTo` then, and a process restart
/// is recommended so pooled connections / background workers rebuild.
export interface RestoreResult {
  restored: boolean;
  migratedFrom: string;
  migratedTo: string;
  restartRecommended: boolean;
}

/// A stored MCP personal access token (the secret is never returned — only a short display prefix).
export interface McpToken {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
}

/// An active OAuth connection (e.g. the claude.ai web MCP connector) - a client the user granted access to.
export interface OAuthConnection {
  id: string;
  clientName: string;
  connectedAt: string | null;
  scopes: string[];
}

/// The response to generating an MCP token: the plaintext token, shown to the user exactly once.
export interface McpTokenCreated {
  id: string;
  name: string;
  prefix: string;
  token: string;
}

/// A built-in chat tool's state for the settings panel.
export interface ChatToolInfo {
  name: string;
  title: string;
  description: string;
  enabled: boolean;
  defaultEnabled: boolean;
}

/// A supported language for content translation (and, when a UI catalog exists, the app UI).
export interface Language {
  code: string;
  englishName: string;
  nativeName: string;
  rtl: boolean;
}

/// The signed-in user's platform authority, resolved server-side from their group membership. Never derived
/// from the JWT: a token claim would keep granting authority until it expired, long after the user left the
/// group.
export interface Permissions {
  manageRooms: boolean;
  manageUsers: boolean;
  managePlatform: boolean;
}

/// A room the signed-in user belongs to. `permissions` is the caller's effective RoomPermission grid as a
/// bitmask (the server sends it as an int precisely so the client can test flags - a string [Flags] value would
/// arrive as "A, B" and break the arithmetic).
export interface RoomListItem {
  id: string;
  name: string;
  kind: number; // 0 = Personal, 1 = Shared
  icon: string | null;
  color: string | null;
  isPersonal: boolean;
  permissions: number;
}

/// RoomPermission flags - mirror src/Diariz.Domain/Entities/RoomPermission.cs (append-only; keep in sync).
export const RoomPermission = {
  ManageRoom: 1,
  CreateRecording: 2,
  RemoveOthersRecordings: 4,
  ShareOut: 8,
  ManageContents: 16,
  EditOthersRecordings: 32,
} as const;

/// RoomPrincipalType - mirror src/Diariz.Domain/Entities/RoomPrincipalType.cs.
export const RoomPrincipalType = { User: 0, Group: 1 } as const;

/// A room member (a user or a group principal) with its permission bitmask.
export interface RoomMember {
  principalType: number;
  principalId: string;
  permissions: number;
  /// The resolved user/group name (server-side), or null if the principal no longer exists.
  displayName: string | null;
}

/// The write payload for upserting a member's permissions (no server-resolved fields).
export interface RoomMemberInput {
  principalType: number;
  principalId: string;
  permissions: number;
}

/// A shared room with its membership, for the Manage Rooms editor.
export interface RoomDetail {
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  members: RoomMember[];
}

/// Create/rename a shared room.
export interface RoomInput {
  name: string;
  description?: string | null;
  icon?: string | null;
  color?: string | null;
}

/// A named collection of users carrying platform permissions. Replaces the old account-type roles. The system
/// group (Platform Administrators) cannot be deleted, renamed, or have its permissions changed.
export interface Group {
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  /// Bit flags, mirroring the server's PlatformPermission: 1 = rooms, 2 = users, 4 = platform.
  permissions: number;
  isSystem: boolean;
  memberIds: string[];
}

/// The signed-in user's editable profile (display name + language preferences + free-text profile fields +
/// colour theme). Email is read-only.
export interface UserProfile {
  email: string;
  fullName: string | null;
  nativeLanguage: string | null;
  uiLanguage: string | null;
  googleConnected: boolean; // account is linked to a Google identity
  googleCalendar: boolean; // user granted Google Calendar read access
  jobTitle: string | null;
  companyName: string | null;
  jobDescription: string | null;
  companyDescription: string | null;
  linkedIn: string | null;
  theme: "auto" | "light" | "dark";
  /// Whether the platform has user API access enabled (drives the Preferences "Developers" tab).
  apiAccessEnabled: boolean;
  /// The caller's platform permissions. Optional only because the server marks it nullable; always sent.
  permissions?: Permissions;
}

/// A stored personal REST-API token, listed in Preferences -> Developers. The secret is never returned -
/// only a short display prefix and usage timestamps.
export interface ApiToken {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
}

/// The response to generating an API token: the plaintext token, shown to the user exactly once.
export interface ApiTokenCreated {
  id: string;
  name: string;
  prefix: string;
  token: string;
}

/// One of the user's Google calendars, for the Preferences picker. `selected` is the user's effective
/// choice (whether its events count toward attribution + the Calendar overlay).
export interface GoogleCalendarListItem {
  id: string;
  summary: string | null;
  backgroundColor: string | null;
  primary: boolean;
  selected: boolean;
}

/// One person on a calendar event (organizer or attendee).
export interface CalendarAttendee {
  email: string | null;
  displayName: string | null;
  responseStatus: string | null; // accepted | declined | tentative | needsAction
  organizer: boolean;
  self: boolean;
}

/// A Google Calendar event. The list/match endpoints return the slim fields; a single-event fetch
/// (`getCalendarEvent`) also populates the rich fields (description/location/organizer/attendees).
export interface CalendarEvent {
  id: string;
  summary: string | null;
  start: string; // ISO
  end: string; // ISO
  htmlLink: string | null;
  description?: string | null;
  location?: string | null;
  organizer?: CalendarAttendee | null;
  attendees?: CalendarAttendee[];
  /// Which of the user's calendars the event is on, its name, and its Google background colour (hex) - for
  /// colouring events per calendar. Null on older/slim payloads.
  calendarId?: string | null;
  calendarName?: string | null;
  color?: string | null;
}

/// A Google Calendar meeting matched to a recording by time overlap (same shape as an event).
export type CalendarMatch = CalendarEvent;

/// A recording's persisted link to a calendar event (the stored snapshot). Rich details are fetched
/// live via `getCalendarEvent(eventId)`.
export interface CalendarLink {
  eventId: string;
  /// Which calendar the event is on (primary or a secondary/shared/subscribed id).
  calendarId: string;
  summary: string | null;
  start: string; // ISO
  end: string; // ISO
  htmlLink: string | null;
  linkedManually: boolean;
  /// The linked calendar's Google colour (hex), for tinting the linked icon. Null when unknown.
  color?: string | null;
}

/// An external iCalendar (.ics) feed the user subscribes to. `lastError` is the last fetch failure (null when
/// healthy) so the manager can flag a broken feed.
export interface IcsFeed {
  id: string;
  name: string;
  url: string;
  color: string | null;
  enabled: boolean;
  lastFetchedAt: string | null;
  lastError: string | null;
}

/// Create/update payload for an external .ics feed.
export interface IcsFeedInput {
  name: string;
  url: string;
  color?: string | null;
  enabled?: boolean;
}

export interface UpdateUserProfile {
  fullName: string | null;
  nativeLanguage: string | null;
  uiLanguage: string | null;
  jobTitle: string | null;
  companyName: string | null;
  jobDescription: string | null;
  companyDescription: string | null;
  linkedIn: string | null;
  theme: "auto" | "light" | "dark";
}

export interface UpdateUserSettings {
  /// Tri-state (like the others below): omit/null = leave unchanged, "" = clear the override, value = set.
  /// The personal settings tabs save independently, so each sends only the fields it owns.
  apiBase?: string | null;
  model?: string | null;
  /// Tri-state: undefined/null = leave unchanged, "" = clear, value = set.
  apiKey?: string | null;
  /// Context-window override; null/0 clears it (falls back to the server default).
  contextWindow?: number | null;
  /// Master switch for chat tool calling; omit to leave unchanged.
  toolsEnabled?: boolean;
  /// Explicit per-tool on/off overrides ({ name: enabled }); omit to leave unchanged.
  toolOverrides?: Record<string, boolean>;
  /// Reasoning: send an OpenAI-style reasoning_effort on LLM requests; omit to leave unchanged.
  reasoningEnabled?: boolean;
  /// Reasoning level ("low"|"medium"|"high"); blank clears the per-user override.
  reasoningEffort?: string;
  /// Where a new recording lands; omit to leave unchanged. A non-SpecificFolder mode clears any fixed folder.
  placementMode?: RecordingPlacementMode;
  placementSectionId?: string | null;
}

// ---- Chat ----
export interface ChatTurn {
  role: "user" | "assistant";
  content: string;
}

/// Context-usage snapshot for the dial: tokens used out of the model's window.
export interface ChatUsage {
  model: string;
  contextUsed: number;
  contextTotal: number;
}

export interface SavedChatContext {
  recordingIds: string[];
  attachmentName: string | null;
  attachmentText: string | null;
  /// Pull the selected recordings' attachments (files + URLs) into the chat context.
  includeAttachments?: boolean;
  /// "All meetings" mode: no transcripts pre-loaded; the assistant searches the whole library on demand.
  searchAllMeetings?: boolean;
  /// Folder chat: the conversation was about this folder (its summary/minutes/actions were the context).
  sectionId?: string | null;
}

export interface ChatConversationSummary {
  id: string;
  title: string;
  updatedAt: string;
}

export interface ChatConversation {
  id: string;
  title: string;
  messages: ChatTurn[];
  context: SavedChatContext;
  updatedAt: string;
}

export interface ChatAttachment {
  name: string;
  chars: number;
  text: string;
}

/// A note the chat assistant prepared to save to a transcript as a Markdown attachment (via the
/// add_as_attachment tool), plus the candidate recordings. One → add directly; several → let the user pick.
export interface AttachmentDraft {
  name: string;
  content: string;
  recordings: { id: string; title: string }[];
}
