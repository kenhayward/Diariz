import type { INodePropertyOptions } from "n8n-workflow";

/// Mirrors WebhookEventTypes.Subscribable in src/Diariz.Api/Webhooks/WebhookEventTypes.cs - the events a
/// PERSONAL subscription may have, which are the ones about the credential owner's own data. Event keys are
/// append-only on the server, so this list only ever grows. The internal webhook.ping type is deliberately
/// absent: it is never subscribable, only sent by the "Send test event" action.
///
/// Platform-only events live in PLATFORM_ONLY_EVENT_OPTIONS below - keep the two lists apart, because
/// offering a platform event in Personal scope produces a node that fails with a 400 the moment the
/// workflow is activated.
export const EVENT_OPTIONS: INodePropertyOptions[] = [
  {
    name: "Recording Created",
    value: "recording.created",
    description: "A recording was uploaded or captured, before any transcription",
  },
  {
    name: "Recording Transcribed",
    value: "recording.transcribed",
    description: "The transcript is ready, with speaker labels and timings",
  },
  {
    name: "Transcription Failed",
    value: "recording.transcription_failed",
    description: "A recording could not be transcribed",
  },
  {
    name: "Summary Ready",
    value: "recording.summarized",
    description: "The AI summary is ready, and the text rides along in the event",
  },
  {
    name: "Meeting Minutes Ready",
    value: "recording.minutes_ready",
    description: "The meeting minutes document is ready, and rides along in the event",
  },
  {
    name: "Action Items Ready",
    value: "recording.action_items_ready",
    description: "Action items were extracted from the transcript",
  },
  {
    name: "Tags Ready",
    value: "recording.tags_ready",
    description: "Topic tags were generated for the recording",
  },
  {
    name: "Formula Result Completed",
    value: "formula_result.completed",
    description: "A formula finished and produced a document",
  },
  {
    name: "Formula Result Failed",
    value: "formula_result.failed",
    description: "A formula run failed",
  },
];

/// Mirrors the platform-only additions in WebhookEventTypes.PlatformSubscribable. These fire across ALL
/// users rather than only the credential owner's own data, so Diariz accepts them only on a PLATFORM
/// subscription (POST /api/admin/webhooks), which requires a Platform Administrator. A personal
/// subscription on feedback.submitted would hand one user another user's words, which is why the server
/// keeps the two lists separate and why this node does too.
export const PLATFORM_ONLY_EVENT_OPTIONS: INodePropertyOptions[] = [
  {
    name: "Feedback Received",
    value: "feedback.submitted",
    description:
      "Someone submitted feedback through Provide Feedback. Carries who sent it, the page they were on and the release, so it can be routed or raised as a ticket. Their words are only included when Include Feedback Text is turned on.",
  },
];

/// Everything a PLATFORM subscription may choose: the personal set plus the platform-only additions,
/// matching WebhookEventTypes.PlatformSubscribable on the server.
export const PLATFORM_EVENT_OPTIONS: INodePropertyOptions[] = [
  ...EVENT_OPTIONS,
  ...PLATFORM_ONLY_EVENT_OPTIONS,
];

/// The event keys the publisher delivers regardless of a platform subscription's signal filter
/// (WebhookEventTypes.IsSignalRouted returns false for these). Every other platform event needs at least
/// one signal, and Diariz rejects the subscription without one.
export const SIGNAL_EXEMPT_EVENTS: string[] = ["feedback.submitted"];
