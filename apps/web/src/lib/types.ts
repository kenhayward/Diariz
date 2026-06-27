// Mirrors the .NET API DTOs (src/Diariz.Api/Contracts/ApiDtos.cs).

export type RecordingStatus =
  | "Uploaded"
  | "Queued"
  | "Transcribing"
  | "Transcribed"
  | "Summarizing"
  | "Summarized"
  | "Failed";

export type RecordingSource = "Microphone" | "System";

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
}

export interface SectionDto {
  id: string;
  name: string;
}

export interface SummaryDto {
  model: string;
  text: string;
  createdAt: string;
}

export interface SegmentDto {
  id: string;
  speaker: string;
  speakerDisplay: string;
  startMs: number;
  endMs: number;
  text: string;
}

export interface TranscriptionDto {
  id: string;
  model: string;
  version: number;
  language: string | null;
  createdAt: string;
  segments: SegmentDto[];
}

/// A diarized speaker in a recording: its label, shown name, the enrolled voiceprint it's
/// linked to (if any), and whether the name was applied automatically by identification.
export interface SpeakerInfo {
  label: string;
  displayName: string;
  profileId: string | null;
  identifiedAuto: boolean;
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
  speakerNames: Record<string, string>;
  speakers: SpeakerInfo[];
  current: TranscriptionDto | null;
  summary: SummaryDto | null;
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

/// The signed-in user's storage usage vs quota (bytes).
export interface UserStorage {
  usedBytes: number;
  quotaBytes: number;
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
