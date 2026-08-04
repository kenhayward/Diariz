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

/// The extra event types a PLATFORM automation may choose, mirroring the platform-only additions in
/// WebhookEventTypes.PlatformSubscribable. Kept out of WEBHOOK_EVENT_KEYS above because that list drives the
/// PERSONAL picker, and the server refuses these on a personal subscription: a personal automation delivers
/// events about its owner's own data, and feedback is readable only by a Platform Administrator, so a
/// personal subscription on it would hand one user another user's words.
export const PLATFORM_ONLY_EVENT_KEYS = ["feedback.submitted"] as const;

/// Event types the publisher delivers whatever a platform automation's signal filter says
/// (WebhookEventTypes.IsSignalRouted returns false for these). Everything else needs at least one signal,
/// and the server rejects the subscription without one.
export const SIGNAL_EXEMPT_EVENT_KEYS: readonly string[] = ["feedback.submitted"];

const LABEL_KEYS: Record<
  (typeof WEBHOOK_EVENT_KEYS)[number] | (typeof PLATFORM_ONLY_EVENT_KEYS)[number],
  string
> = {
  "recording.created": "evtRecordingCreated",
  "recording.transcribed": "evtRecordingTranscribed",
  "recording.transcription_failed": "evtRecordingFailed",
  "recording.summarized": "evtRecordingSummarized",
  "recording.minutes_ready": "evtRecordingMinutesReady",
  "recording.action_items_ready": "evtRecordingActionItemsReady",
  "recording.tags_ready": "evtRecordingTagsReady",
  "formula_result.completed": "evtFormulaCompleted",
  "formula_result.failed": "evtFormulaFailed",
  "feedback.submitted": "evtFeedbackSubmitted",
};

/// Builds the labelled event list for the PERSONAL picker. `t` must be bound to the "account" namespace.
export function webhookEvents(t: TFunction): { key: string; label: string }[] {
  return WEBHOOK_EVENT_KEYS.map((key) => ({ key, label: t(LABEL_KEYS[key]) }));
}

/// The same list for the PLATFORM picker, plus the platform-only event types. A superset, so an admin
/// never loses an event by managing a platform automation instead of a personal one.
export function platformWebhookEvents(t: TFunction): { key: string; label: string }[] {
  return [...WEBHOOK_EVENT_KEYS, ...PLATFORM_ONLY_EVENT_KEYS].map((key) => ({
    key,
    label: t(LABEL_KEYS[key]),
  }));
}
