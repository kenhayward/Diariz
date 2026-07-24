import type { INodePropertyOptions } from "n8n-workflow";

/// Mirrors WebhookEventTypes.Subscribable in src/Diariz.Api/Webhooks/WebhookEventTypes.cs. Event keys are
/// append-only on the server, so this list only ever grows. The internal webhook.ping type is deliberately
/// absent: it is never subscribable, only sent by the "Send test event" action.
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
