import type { TFunction } from "i18next";

/// The subscribable outbound event types, mirroring WebhookEventTypes.Subscribable in
/// src/Diariz.Api/Webhooks/WebhookEventTypes.cs. Keys are append-only on the server, so this list
/// only ever grows. Shared by the personal Automations section and the platform-automations admin
/// section so the two pickers cannot drift apart.
export const WEBHOOK_EVENT_KEYS = [
  "recording.created",
  "recording.transcribed",
  "recording.transcription_failed",
  "recording.summarized",
  "recording.minutes_ready",
  "recording.action_items_ready",
  "recording.tags_ready",
  "formula_result.completed",
  "formula_result.failed",
] as const;

const LABEL_KEYS: Record<(typeof WEBHOOK_EVENT_KEYS)[number], string> = {
  "recording.created": "evtRecordingCreated",
  "recording.transcribed": "evtRecordingTranscribed",
  "recording.transcription_failed": "evtRecordingFailed",
  "recording.summarized": "evtRecordingSummarized",
  "recording.minutes_ready": "evtRecordingMinutesReady",
  "recording.action_items_ready": "evtRecordingActionItemsReady",
  "recording.tags_ready": "evtRecordingTagsReady",
  "formula_result.completed": "evtFormulaCompleted",
  "formula_result.failed": "evtFormulaFailed",
};

/// Builds the labelled event list for a picker. `t` must be bound to the "account" namespace.
export function webhookEvents(t: TFunction): { key: string; label: string }[] {
  return WEBHOOK_EVENT_KEYS.map((key) => ({ key, label: t(LABEL_KEYS[key]) }));
}
