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

export interface RecordingDetail {
  id: string;
  title: string;
  name: string | null;
  source: RecordingSource;
  durationMs: number;
  status: RecordingStatus;
  error: string | null;
  createdAt: string;
  speakerNames: Record<string, string>;
  current: TranscriptionDto | null;
  summary: SummaryDto | null;
}

export interface AuthResponse {
  accessToken: string;
  expiresAt: string;
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
