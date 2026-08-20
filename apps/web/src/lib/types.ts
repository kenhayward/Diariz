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
  /// Wall clock the capture began. Null for uploaded files and recordings made before this was tracked -
  /// fall back to `createdAt`. Prefer this wherever you mean "when the meeting happened": `createdAt` is
  /// upload time, so for a recorded take it is roughly when it *stopped*.
  startedAt?: string | null;
  /// Wall clock the capture stopped. Null when unknown. Distinct from `durationMs`, which is recorded-audio
  /// length and excludes any paused stretches.
  endedAt?: string | null;
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
export interface TemplateContent {
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
  /// The formula whose template generates the minutes. A meeting type carries no prompts of its own.
  primaryFormulaId: string | null;
  /// Formulas run alongside the minutes in the same pipeline; their results land in the Formulas tab.
  additionalFormulaIds: string[];
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
  primaryFormulaId: string | null;
  additionalFormulaIds: string[];
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
  /// The folder's actual room - resolve permissions (e.g. ManageContents for folder-direct attachments)
  /// against THIS, not the current room the URL happens to name (the room-less legacy /sections/:id
  /// deep-link falls back to the caller's personal room, which is not necessarily where this folder lives).
  roomId: string;
  stats: SectionStats;
  summary: FolderSummary | null;
  minutes: FolderMinutes | null;
  meetingTypeId: string | null;
}
/// One note aggregated for the folder Notes tab (carries its source recording's display name).
/// `recordedByUserId` is the recording's owner - the mutating routes are owner-only, so the folder page
/// compares this against `useAuth().id` (the same pattern RecordingDetail uses) to hide edit/delete for a
/// room co-viewer's rows, which come from someone else's recording.
export interface SectionNoteItem {
  id: string;
  recordingId: string;
  recordingName: string;
  text: string;
  capturedAtMs: number | null;
  ordinal: number;
  createdAt: string;
  recordedByUserId: string;
}
/// One attachment aggregated for the folder Attachments tab (carries its source recording's display name).
/// `recordedByUserId` - see `SectionNoteItem` for the rationale.
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
  recordedByUserId: string;
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
  personId: string | null;
  /// The person's details, when this speaker was identified as someone. All null for an anonymous speaker
  /// and for a "Multiple Speakers" slot, which is not one person.
  title: string | null;
  companyName: string | null;
  email: string | null;
  phone: string | null;
  isInternal: boolean | null;
  identifiedAuto: boolean;
  /// The user has marked this slot as overlapping/simultaneous speech ("Multiple Speakers").
  /// Such a speaker is never auto-identified or enrolled into a voiceprint.
  isMultiSpeaker: boolean;
}

/// Someone who appears in meetings. Platform-wide, and the voiceprint is optional: `hasVoiceprint` is false
/// for a person added by hand, or one who opted out and had theirs erased.
export interface Person {
  id: string;
  name: string;
  title: string | null;
  companyName: string | null;
  email: string | null;
  phone: string | null;
  isInternal: boolean;
  voiceprintOptOut: boolean;
  hasVoiceprint: boolean;
  sampleCount: number;
  linkedUserId: string | null;
  isSelf: boolean;
  /// The server's own answer to "may I opt this person out / erase their voiceprint" (ManagePeople, or it
  /// is you). Render those controls from this - do NOT recompute the rule here, or the two will drift.
  canManageBiometrics: boolean;
  createdAt: string;
  updatedAt: string;
}

/// One voice sample feeding a person's voiceprint (the recording-speaker it came from).
export interface VoiceSample {
  id: string;
  recordingId: string;
  recordingName: string;
  speakerLabel: string;
  /// Start (ms) of that speaker's first segment, so the UI can play a sample of the voice.
  startMs: number;
  createdAt: string;
}

/// A group of people who look like the same human. `reason` is "email" or "name".
export interface PersonDuplicateGroup {
  reason: string;
  people: Person[];
}

/// A person with their voiceprint's training provenance and how many recording-speakers they label.
export interface PersonDetail {
  person: Person;
  identifiedCount: number;
  samples: VoiceSample[];
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

/// The part of a not-yet-uploaded capture that the notes UI needs: enough to show a thumbnail and to
/// delete it by name. `PendingShot` (lib/pendingScreenshots.ts) satisfies this structurally, and it is
/// also what crosses the pop-out channel - the full-resolution PNG never leaves the main window.
export interface ShotView {
  id: string;
  capturedAtMs: number;
  thumb: Blob;
}

/// A screen capture taken during a recording (desktop client only). capturedAtMs is the offset into the
/// recording clock; immutable after capture. Image bytes come from the content and thumb URLs.
export interface Screenshot {
  id: string;
  capturedAtMs: number;
  width: number;
  height: number;
  sizeBytes: number;
  ordinal: number;
  createdAt: string;
}

/// An action across the whole library (the "Actions" tab), carrying its source recording so the row can
/// link back to that transcript. `recordedByUserId` - see `SectionNoteItem` for the rationale (the folder
/// page's aggregated Actions tab hides edit/delete/complete for someone else's recording).
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
  recordedByUserId: string;
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
  /// The spoken language this recording is pinned to (BCP-47), or null when Whisper detects it.
  transcriptionLanguage: string | null;
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
  /// Wall clock the capture began. Null for uploaded files and recordings made before this was tracked -
  /// fall back to `createdAt`. Prefer this wherever you mean "when the meeting happened".
  startedAt?: string | null;
  /// Wall clock the capture stopped. Null when unknown. Distinct from `durationMs`, which excludes pauses.
  endedAt?: string | null;
  /// Tags the user adopted on this recording, in adoption order. The only tags the tag cloud counts.
  tags?: string[];
  /// Automatically suggested tags nobody has accepted or dismissed yet, heaviest first - the hub's hints.
  suggestedTags?: string[];
  /// Whether the caller may add/remove/dismiss this recording's tags - the owner, or a room member holding
  /// `EditOthersRecordings`. Absent/false renders the Tags popover read-only; treat it that way explicitly
  /// (`?? false`) rather than assuming presence, the same fail-closed default the server uses.
  canEditTags?: boolean;
}

/// A room a recording sits in, for the detail Overview. `isMain` = the recorder's personal (home) room -
/// the only room it can be deleted from.
export interface RecordingRoom {
  id: string;
  name: string;
  icon: string | null;
  color: string | null;
  isMain: boolean;
  /// The folder this recording sits in **within this room** (null = the room's top level). Per-room because
  /// a recording shared into several rooms is filed independently in each - the detail page's folder chips
  /// read the entry for the room being viewed, never a single "the recording's folder".
  sectionId: string | null;
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
  /// Avatar from the linked Google account, or null - then the admin console shows initials, as the
  /// account menu does.
  pictureUrl: string | null;
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
  /// Master switch for Claude / MCP access (personal MCP tokens). On by default.
  mcpAccessEnabled: boolean;
  /// Master switch for outbound webhooks (meeting-event automations). Off by default.
  webhooksEnabled: boolean;
  /// Per-request timeout (seconds) applied to every LLM call platform-wide. Default 120.
  llmTimeoutSeconds: number;
  /// Master switch for the LLM usage log. On by default.
  llmUsageLoggingEnabled: boolean;
  /// Usage rows older than this many days are deleted by the nightly sweep. 0 = keep forever. Default 90.
  llmUsageRetentionDays: number;
  /// Whether streaming requests ask for token counts (stream_options.include_usage). Not yet consumed by
  /// this release - wired for the next one. On by default.
  llmStreamUsageEnabled: boolean;
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
  /// The context window (tokens) of the model serving chat, for the chat dial. READ-ONLY from 0.221.0:
  /// LLM configuration is platform-wide and edited at /admin/llm-models.
  contextWindow: number;
  /// The model serving chat, for the dial's label before the first turn reports one. Read-only, and
  /// derived from `chatModelId` below - it names the model that will actually answer, not the platform's.
  chatModel: string;
  /// The model chosen in the chat picker, or null to follow the platform's chat routing. Writable, unlike
  /// the two fields above, which are derived from it.
  chatModelId: string | null;
  /// Effective master switch for chat tool calling (user override ?? server default).
  toolsEnabled: boolean;
  defaultToolsEnabled: boolean;
  /// The catalog of built-in chat tools with their resolved on/off state.
  tools: ChatToolInfo[];
  /// Where a new recording lands in the user's Personal room (enum name on the wire).
  placementMode: RecordingPlacementMode;
  placementSectionId: string | null;
  /// True when the server has an STT endpoint configured (dictation server-fallback path is available).
  dictationServerAvailable: boolean;
  /// Whether the user has opted in to mirroring a desktop Outlook calendar. Off by default.
  outlookSyncEnabled: boolean;
  /// Whether a recording started from a calendar event ends by itself. Off by default; the two settings
  /// below are its conditions and are inert while it is false.
  calendarAutoStopEnabled: boolean;
  /// Minutes to keep recording past the invite's end time.
  calendarAutoStopAfterMinutes: number;
  /// Seconds of continuous silence that also ends such a recording.
  calendarSilenceStopSeconds: number;
  /// Whether consecutive same-speaker segments are collapsed automatically once a recording finishes
  /// transcribing. Off by default.
  autoMergeSpeakerSegments: boolean;
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

/// How far the server has got assembling a backup archive. The download sends no bytes until the whole zip is
/// built, so this is the only way the browser can tell that a backup is under way.
export interface BackupStatus {
  running: boolean;
  phase: "Database" | "Objects" | null;
  objectsArchived: number;
  startedAt: string | null;
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
  manageFormulas: boolean;
  /// Browse the people directory, and edit, delete or merge people other than yourself. Does not gate your
  /// own biometric choices: opting out and erasing a voiceprint are always allowed on your own person.
  managePeople: boolean;
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
  /// Folders in this room.
  sectionCount: number;
  /// Recordings placed in this room. A recording shared into several rooms counts in each - the number says
  /// what you'll find in there, not how many distinct recordings exist.
  recordingCount: number;
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
  /// Default spoken language for new recordings (BCP-47), or null to detect it per recording.
  transcriptionLanguage: string | null;
  googleConnected: boolean; // account is linked to a Google identity
  googleCalendar: boolean; // user granted Google Calendar read access
  jobTitle: string | null;
  companyName: string | null;
  jobDescription: string | null;
  companyDescription: string | null;
  linkedIn: string | null;
  theme: "auto" | "light" | "dark";
  /// Whether the platform has user API access enabled (drives the API card on Preferences -> Integrations).
  apiAccessEnabled: boolean;
  /// The caller's platform permissions. Optional only because the server marks it nullable; always sent.
  permissions?: Permissions;
  /// Drives the Automations card on Preferences -> Integrations.
  webhooksEnabled: boolean;
  /// Drives the MCP card on Preferences -> Integrations. Optional because a server older than the flag
  /// omits it, and MCP defaults to on - a missing value must not switch the card off.
  mcpAccessEnabled?: boolean;
}

/// A stored personal REST-API token, listed in Preferences -> Developers. The secret is never returned -
/// only a short display prefix and usage timestamps.
export interface ApiToken {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  /// Whether the token can only read (ReadOnly) or also write (ReadWrite).
  scope: "ReadOnly" | "ReadWrite";
  /// When the token stops working, or null if it never expires.
  expiresAt: string | null;
}

/// The response to generating an API token: the plaintext token, shown to the user exactly once.
export interface ApiTokenCreated {
  id: string;
  name: string;
  prefix: string;
  token: string;
}

/// Options when minting a new API token: restrict it to read-only, and/or give it an expiry.
export interface CreateApiTokenOptions {
  readOnly: boolean;
  expiresAt: string | null;
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
  /// A date-only entry (holiday, birthday, "out of office" day) rather than a timed meeting. Sent by the
  /// API for Google, .ics and Outlook events alike; the day grid pins these above the time axis rather
  /// than placing them on it. Null/absent on older payloads, where `dayItemSpan` falls back to a
  /// midnight-to-midnight heuristic.
  allDay?: boolean;
  /// One occurrence of a repeating series. Reported for Google, `.ics` and mirrored Outlook alike. The series
  /// *key* is deliberately not sent: the browser never needs it, because the sibling lookup is resolved
  /// server-side from the event id (`getSeriesRecordings`).
  recurring?: boolean;
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

/// One earlier recording of the same recurring meeting. The times are the *occurrence's* (from the link
/// snapshot), not the upload's, so the list reads as a calendar history.
export interface SeriesRecording {
  id: string;
  title: string;
  name: string | null;
  startsAt: string; // ISO
  endsAt: string; // ISO
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

// ---- Desktop Outlook mirror ----

/// One machine that has connected its local desktop Outlook calendar. `lastSyncedAt` / `lastError` /
/// `eventCount` are the health triple shown in Preferences, mirroring a feed's.
export interface OutlookSource {
  id: string;
  deviceId: string;
  deviceName: string | null;
  mailboxName: string | null;
  displayName: string;
  color: string | null;
  enabled: boolean;
  pastDays: number;
  futureDays: number;
  skipPrivate: boolean;
  includeBody: boolean;
  lastSyncedAt: string | null;
  lastError: string | null;
  eventCount: number;
}

/// Partial update for a connected device - omit a field to leave it unchanged.
export interface OutlookSourceInput {
  displayName?: string;
  color?: string | null;
  enabled?: boolean;
  pastDays?: number;
  futureDays?: number;
  skipPrivate?: boolean;
  includeBody?: boolean;
  lastError?: string | null;
}

export interface OutlookAttendeeInput {
  name?: string | null;
  email?: string | null;
  /// Google's vocabulary (accepted/declined/tentative/needsAction), mapped on the desktop.
  response?: string | null;
  optional?: boolean;
}

/// One flattened occurrence as the desktop reports it. All-day entries carry `startDate`/`endDate` as **local**
/// yyyy-MM-dd strings - deriving them from the UTC instant is the classic off-by-one.
export interface OutlookEventInput {
  uid: string;
  subject?: string | null;
  start: string;
  end: string;
  allDay?: boolean;
  startDate?: string | null;
  endDate?: string | null;
  windowsTimeZoneId?: string | null;
  location?: string | null;
  onlineMeetingUrl?: string | null;
  bodyText?: string | null;
  categories?: string | null;
  sensitivity?: number;
  busyStatus?: number;
  isRecurring?: boolean;
  organizerName?: string | null;
  organizerEmail?: string | null;
  attendees?: OutlookAttendeeInput[];
  lastModified?: string | null;
}

/// One page of a sync run. The desktop sends the whole window and the server reconciles; `final` marks the
/// last page and `complete` says the read finished cleanly - the server only deletes when both hold.
export interface OutlookSyncRequest {
  syncId: string;
  device: { deviceId: string; deviceName?: string; mailboxName?: string; timeZone?: string };
  windowStart: string;
  windowEnd: string;
  events: OutlookEventInput[];
  complete?: boolean;
  pageIndex?: number;
  final?: boolean;
}

export interface OutlookSyncResult {
  sourceId: string;
  syncId: string;
  created: number;
  updated: number;
  unchanged: number;
  deleted: number;
  skipped: number;
  eventCount: number;
  syncedAt: string;
}

export interface UpdateUserProfile {
  fullName: string | null;
  nativeLanguage: string | null;
  uiLanguage: string | null;
  transcriptionLanguage: string | null;
  jobTitle: string | null;
  companyName: string | null;
  jobDescription: string | null;
  companyDescription: string | null;
  linkedIn: string | null;
  theme: "auto" | "light" | "dark";
}

export interface UpdateUserSettings {
  /// The personal settings tabs save independently, so each sends only the fields it owns - omitting a
  /// field always means "leave it unchanged", never "clear it".
  ///
  /// Master switch for chat tool calling; omit to leave unchanged.
  toolsEnabled?: boolean;
  /// Explicit per-tool on/off overrides ({ name: enabled }); omit to leave unchanged.
  toolOverrides?: Record<string, boolean>;
  /// Where a new recording lands; omit to leave unchanged. A non-SpecificFolder mode clears any fixed folder.
  placementMode?: RecordingPlacementMode;
  placementSectionId?: string | null;
  /// The desktop Outlook opt-in; omit to leave unchanged. Setting it false also **erases** every connected
  /// device and the meetings mirrored from them - it is a privacy switch, not a pause.
  outlookSyncEnabled?: boolean;
  /// Whether a recording started from a calendar event ends by itself; omit to leave unchanged.
  calendarAutoStopEnabled?: boolean;
  /// Minutes past the invite's end; omit to leave unchanged. A non-positive value resets to the default.
  calendarAutoStopAfterMinutes?: number;
  /// Seconds of silence that ends the recording; omit to leave unchanged. Non-positive resets to the default.
  calendarSilenceStopSeconds?: number;
  /// Whether transcripts are auto-merged by speaker; omit to leave unchanged.
  autoMergeSpeakerSegments?: boolean;
  /// Tri-state: omit = leave unchanged, 0 = clear the override, 5+ = set it (1-4 is rejected by the server).
  llmTimeoutSeconds?: number;
  /// The chat model picker's choice; omit to leave unchanged. An all-zero GUID clears it and follows the
  /// platform's chat routing.
  chatModelId?: string | null;
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
  /// The model this conversation was using when saved. Null for conversations saved before 0.231.0, and
  /// for one on the platform default. Restored on reopen, falling back to the default when the model is
  /// no longer offered.
  modelId?: string | null;
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

// ---- Formulas ----

/// A formula's sharing scope: Personal (owned by its creator), Platform (shared, admin-managed), or Diariz
/// (seeded built-in, admin-toggleable but never deletable).
export type FormulaScope = "Personal" | "Platform" | "Diariz";

/// A saved template + context. `context` is the FormulaContext [Flags] bit value as a plain number - NOT the
/// enum name (see CLAUDE.md's "Flags enum serializes as string" gotcha).
export interface Formula {
  id: string;
  scope: FormulaScope;
  ownerUserId: string | null;
  name: string;
  description: string | null;
  /// The structured template (the same shape a meeting type's minutes template uses). A formula that is just a
  /// prompt is one headless (level-0) section holding one prompt block - see `lib/formulaTemplate.ts`.
  content: TemplateContent;
  context: number;
  enabled: boolean;
  isBuiltIn: boolean;
  shared: boolean;
  /// Workflow signal ids attached to this formula (drives webhook signalFilter matching downstream).
  signals: string[];
}

/// A formula shared by another user, for the discovery browser (mirrors SharedFormulaDto).
export interface SharedFormula {
  formula: Formula;
  ownerName: string | null;
  ownerPictureUrl: string | null;
  alreadyAdded: boolean;
}

/// Where a formula result came from, for the runs-list icon. Mirrors FormulaResultOriginDto.
export interface FormulaResultOrigin {
  kind: "diariz" | "platform" | "personal"; // a subscribed shared formula resolves to "personal" (the sharer)
  personName: string | null;
  personPictureUrl: string | null;
}

/// A formula's saved output on a recording. `name`/timestamps only - the generated Markdown body is fetched
/// separately via `getFormulaResultText`. Formula runs are async: a result appears as `Generating`, then
/// becomes `Ready` (body available) or `Failed` (`error` carries the reason).
export interface FormulaResult {
  id: string;
  recordingId: string;
  name: string;
  /// Async lifecycle: "Generating" (in flight, no body yet), "Ready" (body available), "Failed".
  status: "Generating" | "Ready" | "Failed";
  /// The failure reason when `status === "Failed"`; null otherwise.
  error: string | null;
  createdByUserId: string | null;
  createdAt: string;
  updatedAt: string;
  origin: FormulaResultOrigin;
}

/// A formula's saved output on a section (folder). Same shape as `FormulaResult`, but scoped to a section
/// (`sectionId`) rather than a recording. The generated Markdown body is fetched separately via
/// `getSectionFormulaResultText`.
export interface SectionFormulaResult {
  id: string;
  sectionId: string;
  name: string;
  status: "Generating" | "Ready" | "Failed";
  error: string | null;
  createdByUserId: string | null;
  createdAt: string;
  updatedAt: string;
  origin: FormulaResultOrigin;
}

/// The fields shared by every formula result (recording or section), so components that only render the
/// common metadata (FormulasManager, FormulasPanel) can accept either kind without caring about the target.
export interface FormulaResultLike {
  id: string;
  name: string;
  createdAt: string;
  origin: FormulaResultOrigin;
  status: FormulaResult["status"];
  error: string | null;
}

/// FormulaContext bit flags - mirror src/Diariz.Domain/Entities/FormulaContext.cs (append-only; keep in sync).
export const FormulaContextBits = {
  Transcript: 1,
  Notes: 2,
  Attachments: 4,
  Summary: 8,
  Minutes: 16,
  Actions: 32,
} as const;

// ---- Search ----
/// A folder whose name matched the query. Mirrors FolderHitDto.
export interface FolderHit {
  id: string;
  name: string;
  parentId: string | null;
  roomId: string;
  roomName: string;
  /// Ancestor names, root-first, excluding this folder's own name.
  breadcrumb: string[];
  /// Everything underneath, including sub-folders' recordings.
  recordingCount: number;
}

/// A recording whose transcript matched, with its best-matching passage. Mirrors RecordingSearchHitDto.
/// `snippet` is plain text - the client highlights the query itself, so the server never ships markup.
export interface RecordingSearchHit {
  recordingId: string;
  name: string;
  createdAt: string;
  durationMs: number;
  sectionId: string | null;
  sectionName: string | null;
  /// The folder path, root-first. Empty when the recording is in no folder.
  breadcrumb: string[];
  snippet: string | null;
  /// Where the snippet sits in the recording, for deep-linking to the moment.
  snippetStartMs: number;
  speakerName: string | null;
  score: number;
}

export interface SearchResponse {
  query: string;
  /// What was actually searched: "folder" | "room" | "everywhere".
  scope: string;
  folders: FolderHit[];
  recordings: RecordingSearchHit[];
}

/// The scope a search runs in. `folder` is the drill position, `everywhere` spans every room.
export type SearchScope = "folder" | "everywhere";

// ---- Webhooks (outbound automations) ----
export interface WebhookSubscription {
  id: string;
  name: string;
  url: string;
  eventTypes: string[];
  isActive: boolean;
  consecutiveFailures: number;
  includeAttendeeContacts: boolean;
  disabledReason: string | null;
  lastDeliveryAt: string | null;
  lastStatus: string | null;
  createdAt: string;
  /// Workflow signal keys this webhook filters deliveries to (empty = no signal filtering).
  signalFilter: string[];
  /// Personal (owned by a user) or Platform (admin-managed, `/api/admin/webhooks`).
  scope: "Personal" | "Platform";
  /// Platform only: whether a `feedback.submitted` delivery carries the submitter's own words. Off unless
  /// asked for - the description is free text and may quote meeting content.
  includeFeedbackText: boolean;
}
/// The response to creating a webhook: the plaintext signing secret, shown to the user exactly once.
export interface WebhookCreated {
  id: string;
  name: string;
  url: string;
  eventTypes: string[];
  secret: string;
}
export interface WebhookDelivery {
  id: string;
  eventType: string;
  status: string;
  attemptCount: number;
  responseStatus: number | null;
  lastError: string | null;
  createdAt: string;
  nextAttemptAt: string | null;
}
export interface CreateWebhookBody {
  name: string;
  url: string;
  eventTypes: string[];
  /// Send attendees' email addresses and phone numbers in the payload. Off unless explicitly set - an
  /// automation posts to an arbitrary URL, so contact details are opt-in per subscription.
  includeAttendeeContacts?: boolean;
}
export interface UpdateWebhookBody extends CreateWebhookBody {
  isActive: boolean;
}

/// Create/update payload for a Platform (admin-managed) webhook - `/api/admin/webhooks`.
export interface CreatePlatformWebhookBody {
  name: string;
  url: string;
  eventTypes: string[];
  signalFilter: string[];
  /// Include the submitter's words in a `feedback.submitted` delivery. Omitted reads as false on the
  /// server, so an older client cannot silently turn it on.
  includeFeedbackText?: boolean;
}

// ---- Workflow signals ----
/// A workflow signal: a named condition a formula can attach, driving webhook `signalFilter` matching.
export interface WorkflowSignal {
  id: string;
  key: string;
  label: string;
  description: string | null;
  isActive: boolean;
}
export interface CreateWorkflowSignalBody {
  key: string;
  label: string;
  description: string | null;
}
export interface UpdateWorkflowSignalBody {
  label: string;
  description: string | null;
  isActive: boolean;
}

// ---- LLM usage log (Platform Administrator) ----
// Mirrors src/Diariz.Api/Contracts/LlmUsageContracts.cs and LlmUsageController.

/// What an LLM call was for. The API serializes LlmCallKind by name (JsonStringEnumConverter), so this
/// is a string union of every enum member - the shape used in BOTH directions: response rows report it
/// this way, and LlmUsageFilter.kinds (below) is filtered by it too, since the server binds `kinds` as
/// LlmCallKind[] and ASP.NET Core's query-string enum binder accepts names directly. No name-to-number
/// translation is needed anywhere.
///
/// Declared as a const array with the type derived from it, rather than a hand-written union, because the
/// deep-link parser has to VALIDATE a kind at runtime - and two hand-maintained copies of the same list is
/// exactly the drift `llmCallKind.test.ts` exists to catch.
export const LLM_CALL_KINDS = [
  "Unknown",
  "Summarize",
  "SectionSummary",
  "MeetingMinutes",
  "SectionMinutes",
  "MeetingTypeMinutes",
  "ExtractActions",
  "Tags",
  "Translation",
  "Dictation",
  "Embedding",
  "SearchQuery",
  "ChatMessage",
  "FormulaRun",
  "ChatTitle",
  "AdminTest",
] as const;

export type LlmCallKind = (typeof LLM_CALL_KINDS)[number];

/// Column a usage-log list request may sort by (LlmUsageQuery.SortWhitelist's keys).
export type LlmUsageSortKey =
  | "startedAt"
  | "durationMs"
  | "promptTokens"
  | "completionTokens"
  | "totalTokens"
  | "kind"
  | "model"
  | "userEmail";

/// Dimension the roll-up summary may group by (LlmUsageQuery.GroupByWhitelist's keys).
export type LlmUsageGroupDimension = "user" | "model" | "kind";

/// Filter shared by every /api/admin/llm-usage endpoint. `userIds`/`kinds`/`models` missing or empty both
/// mean "no filter on this dimension" - never send an empty array meaning "match nothing". `kinds` is
/// typed `LlmCallKind[]` - the same string-name shape a response row's own `kind` field uses - and is
/// sent as the enum NAME on the wire (e.g. `?kinds=Tags`); the API binds `[FromQuery] LlmCallKind[]?`,
/// and ASP.NET Core's query-string enum binder accepts names directly, so no name-to-number translation
/// is needed to feed a value from `getLlmUsageFilters()` back into this filter. `from`/`to` are ISO 8601
/// strings; omitting `from` defaults server-side to 30 days before now.
export interface LlmUsageFilter {
  from?: string | null;
  to?: string | null;
  userIds?: string[];
  kinds?: LlmCallKind[];
  models?: string[];
  outcome?: "ok" | "failed" | "all" | null;
  recordingId?: string | null;
  sectionId?: string | null;
}

/// Aggregate totals over a filtered LlmCalls query. The token sums and `tokensPerSecond` are
/// `number | null` - never `number | undefined`, never a bare `number` - because null means "nothing in
/// this set reported a value", which must never render as "0 tokens". `tokenMeasuredCalls` is how many
/// calls reported ANY token count at all (prompt, completion, reasoning, or total), a coarser question
/// than any single column's own measured count.
///
/// `promptTokensMeasured`/`completionTokensMeasured`/`reasoningTokensMeasured`/`totalTokensMeasured` are
/// that narrower, per-column count - always `number` (never null; a count of zero is a legitimate answer,
/// not an absence of data). Use these, never `tokenMeasuredCalls`, to caption a SPECIFIC column's own
/// total ("measured on N of M calls") - the four columns are independently nullable and in practice very
/// unevenly populated (most models never report reasoning tokens at all), so captioning every column with
/// the same any-column figure states something false about at least three of the four columns whenever
/// they differ. `tokenMeasuredCalls` remains the right number for qualifying the set as a whole.
export interface LlmUsageTotals {
  calls: number;
  operations: number;
  durationMs: number;
  promptTokens: number | null;
  completionTokens: number | null;
  reasoningTokens: number | null;
  totalTokens: number | null;
  tokenMeasuredCalls: number;
  promptTokensMeasured: number;
  completionTokensMeasured: number;
  reasoningTokensMeasured: number;
  totalTokensMeasured: number;
  failedCalls: number;
  tokensPerSecond: number | null;
}

/// One LlmCalls row, as returned by mode=calls. Per-call token fields are `number | null`: null means this
/// call reported no usage for that field, distinct from a measured zero.
export interface LlmUsageCallRow {
  id: string;
  operationId: string;
  sequence: number;
  kind: LlmCallKind;
  userId: string | null;
  userEmail: string;
  recordingId: string | null;
  recordingTitle: string | null;
  sectionId: string | null;
  sectionName: string | null;
  model: string;
  startedAt: string;
  completedAt: string;
  durationMs: number;
  promptTokens: number | null;
  completionTokens: number | null;
  reasoningTokens: number | null;
  totalTokens: number | null;
  success: boolean;
  statusCode: number | null;
  errorKind: string | null;
  /// The server's finish_reason - "stop", "length", "tool_calls", "content_filter" - or null when it
  /// reported none. Null is NOT the same as "stop": plenty of compatible servers omit the field.
  finishReason: string | null;
  /// The reply was cut off by a token cap. Derived server-side from finishReason so there is one
  /// definition of it. A truncated call is still a SUCCESS - a 200 with tokens billed - so it is a
  /// separate signal from the error state, not a kind of failure.
  truncated: boolean;
  /// This call's own generation rate, completionTokens over durationMs. Null when the server reported no
  /// completion tokens or the duration was zero - never 0 (which would read as "generated nothing") and
  /// never Infinity. A measured zero IS 0, not null.
  tokensPerSecond: number | null;
}

/// One operation - every LlmCalls row sharing an operationId, collapsed to a single row, as returned by
/// mode=operations (the default). Token fields are `number | null`, same rule as LlmUsageCallRow.
export interface LlmUsageOperationRow {
  operationId: string;
  kind: LlmCallKind;
  userId: string | null;
  userEmail: string;
  recordingId: string | null;
  recordingTitle: string | null;
  sectionId: string | null;
  sectionName: string | null;
  model: string;
  turns: number;
  startedAt: string;
  completedAt: string;
  promptTokens: number | null;
  completionTokens: number | null;
  reasoningTokens: number | null;
  totalTokens: number | null;
  success: boolean;
  /// True when ANY call in this operation stopped on "length". Rolled up because one truncated call is
  /// the whole reason to look, and operations is the default view.
  truncated: boolean;
  /// Time the model actually spent on this operation: the SUM of its calls' durations. Deliberately NOT
  /// the wall-clock span between startedAt and completedAt, which for a multi-call operation includes the
  /// gaps between calls (tool execution in a chat turn). It is the denominator of tokensPerSecond, shown
  /// so the rate reconciles with something visible on the same line.
  durationMs: number;
  /// SUM(completionTokens) / SUM(durationMs) across this operation's calls - the same formula the totals
  /// row and the summary use, never an average of the calls' own rates.
  tokensPerSecond: number | null;
}

/// One page of `TRow`, plus totals over the WHOLE filtered set (not just the page - see LlmUsageTotals)
/// and `total`, the row/operation count of the whole filtered set before paging.
export interface LlmUsagePage<TRow> {
  rows: TRow[];
  page: number;
  pageSize: number;
  total: number;
  totals: LlmUsageTotals;
}

/// One roll-up row from GET /api/admin/llm-usage/summary, grouped by whichever of user/model/kind was
/// requested. A dimension NOT requested is null on every row; a dimension that IS requested is never null
/// on a real row. `averageTurnsPerOperation`/`maxTurnsPerOperation` are computed PER OPERATION, never
/// summed across the operations in the group.
export interface LlmUsageSummaryGroup {
  userId: string | null;
  userEmail: string | null;
  model: string | null;
  kind: LlmCallKind | null;
  calls: number;
  operations: number;
  averageTurnsPerOperation: number;
  maxTurnsPerOperation: number;
  promptTokens: number | null;
  completionTokens: number | null;
  reasoningTokens: number | null;
  totalTokens: number | null;
  tokenMeasuredCalls: number;
  failedCalls: number;
  tokensPerSecond: number | null;
}

/// Response for GET /api/admin/llm-usage/summary: one row per requested group, plus totals over the whole
/// filtered set (never derived by folding over `groups`).
export interface LlmUsageSummary {
  groups: LlmUsageSummaryGroup[];
  totals: LlmUsageTotals;
}

/// One user with at least one LlmCalls row in the scoped set, for the viewer's user filter dropdown.
export interface LlmUsageFilterUser {
  userId: string;
  userEmail: string;
}

/// Response for GET /api/admin/llm-usage/filters: the distinct users, models and kinds that actually
/// occur in the scoped set - never every enum value or every user in the system.
export interface LlmUsageFilterOptions {
  users: LlmUsageFilterUser[];
  models: string[];
  kinds: LlmCallKind[];
}

// ---- Feedback ----
/// A user-submitted "something looks or behaves wrong" report, as returned to a Platform Administrator.
/// `trailJson` is the client-scrubbed action trail (see `lib/trail`) serialized to JSON - stored verbatim
/// from the client, so it must be parsed defensively rather than trusted.
export interface FeedbackDto {
  id: string;
  userId: string;
  userEmail: string | null;
  createdAt: string;
  description: string;
  route: string;
  release: string;
  trailJson: string;
}

// ---- Platform LLM models (the /admin/llm-models page) ----

/// One configured model. `parameters` maps a call-group name to that group's parameter-layer JSON, with
/// `ModelBase` holding the model's own defaults; a group absent from the map has no override.
///
/// The key is never sent to the client - only `hasApiKey` - so the editor must omit `apiKey` on save
/// rather than send back an empty string, which the API reads as "clear it".
export interface LlmModel {
  id: string;
  name: string;
  /// A user-facing name shown in place of the slug. Null or blank means "use the slug".
  displayName: string | null;
  apiBase: string;
  hasApiKey: boolean;
  contextLength: number;
  /// Whether this model appears in the chat model picker. Written through its own endpoint
  /// (`setModelChatEnabled`), never through an upsert - see LlmModelUpsert.
  chatEnabled: boolean;
  parameters: Record<string, string>;
}

/// Note there is no `chatEnabled` here, deliberately: the editor drawer does not show that control, so
/// carrying it would let every save from the drawer post a stale value and silently un-offer the model.
/// One model an endpoint reported, as offered for import. `contextLength` is always usable, but
/// `contextLengthReported` says whether the endpoint supplied it or it was defaulted - the value sizes both
/// the chat dial and the real context budget, so a guess has to be visible as one.
export interface DiscoveredModel {
  id: string;
  contextLength: number;
  contextLengthReported: boolean;
  alreadyExists: boolean;
}

/// What an import did. `needContextLength` names the models whose window was defaulted rather than reported.
export interface ImportModelsResult {
  added: number;
  skipped: number;
  needContextLength: string[];
}

export interface LlmModelUpsert {
  name: string;
  apiBase: string;
  apiKey?: string;
  contextLength: number;
  /// Blank is sent as null, which the server stores as "not set" so the slug shows through.
  displayName?: string | null;
  parameters: Record<string, string>;
}

/// One model the chat picker offers. Endpoint and key are absent by design - this comes from
/// /api/chat/models, which every signed-in user may read, unlike the administrator-only LlmModel listing.
export interface ChatModelOption {
  id: string;
  /// What the user reads. The server falls back to the slug when no display name is set.
  label: string;
  /// The slug the server sends as `model`, so a streamed usage snapshot can be matched back to a label.
  name: string;
  contextLength: number;
  isDefault: boolean;
}

/// One administrator-initiated test call. `response` is the model's actual reply - the only LLM output the
/// API ever returns to a browser, and it is never stored.
///
/// Every token count is nullable because plenty of OpenAI-compatible servers report none, and a missing
/// count is not a zero: rendering it as 0 would state something false about the call.
export interface LlmTestOutcome {
  ok: boolean;
  httpStatus: number | null;
  /// Milliseconds to the first content token. Null when nothing ever arrived.
  ttftMs: number | null;
  durationMs: number;
  promptTokens: number | null;
  completionTokens: number | null;
  reasoningTokens: number | null;
  totalTokens: number | null;
  finishReason: string | null;
  response: string | null;
  /// The body that was actually sent. Never contains the API key.
  requestBodyJson: string;
  /// Timeout | Transport | Http<status>, matching the usage log's vocabulary.
  errorKind: string | null;
  message: string | null;
  /// Which parameter the endpoint blamed, when it named one.
  offendingParameter: string | null;
}

/// Which model serves which call group, plus the fallback for groups with no entry.
export interface LlmAssignments {
  defaultModelId: string | null;
  assignments: Record<string, string>;
}
