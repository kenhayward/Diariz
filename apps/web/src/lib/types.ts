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

export type RecordingSource = "Microphone" | "System" | "Upload";

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
}

export interface SectionDto {
  id: string;
  name: string;
  /// Null for a top-level section; the parent section's id for a sub-section (one level of nesting).
  parentId: string | null;
  /// Manual sort order among siblings.
  position: number;
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
/// the action itself (shown as the "Action" column); `actor`/`deadline` may be empty.
export interface RecordingAction {
  id: string;
  text: string;
  actor: string;
  deadline: string;
  ordinal: number;
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
  /// Extracted action items (only meaningful once `actionsExtracted` is true).
  actions: RecordingAction[];
  /// Whether action extraction has been run — drives the "show the Actions panel by exception" rule.
  actionsExtracted: boolean;
  /// Whether the original audio is still present (false once the audio has been deleted).
  hasAudio: boolean;
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
}

/// The signed-in user's storage usage vs quota (bytes), plus total transcription wall-clock time (ms).
export interface UserStorage {
  usedBytes: number;
  quotaBytes: number;
  totalTranscriptionMs: number;
}

/// Platform-wide storage-quota defaults (bytes), edited by the Platform Administrator.
export interface PlatformSettings {
  starterQuotaBytes: number;
  maxQuotaBytes: number;
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
}

/// A supported language for content translation (and, when a UI catalog exists, the app UI).
export interface Language {
  code: string;
  englishName: string;
  nativeName: string;
  rtl: boolean;
}

/// The signed-in user's editable profile (display name + language preferences). Email is read-only.
export interface UserProfile {
  email: string;
  fullName: string | null;
  nativeLanguage: string | null;
  uiLanguage: string | null;
}

export interface UpdateUserProfile {
  fullName: string | null;
  nativeLanguage: string | null;
  uiLanguage: string | null;
}

export interface UpdateUserSettings {
  apiBase: string | null;
  model: string | null;
  /// Tri-state: undefined/null = leave unchanged, "" = clear, value = set.
  apiKey?: string | null;
  /// Context-window override; null/0 clears it (falls back to the server default).
  contextWindow?: number | null;
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
