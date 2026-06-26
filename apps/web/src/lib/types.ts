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
}

export interface SummaryDto {
  model: string;
  text: string;
  createdAt: string;
}

export interface SegmentDto {
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
