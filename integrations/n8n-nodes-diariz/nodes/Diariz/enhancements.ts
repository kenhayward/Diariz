/// The curated layer over the generated operations.
///
/// Rather than hand-writing a second, parallel set of resources (which would leave the node showing two
/// entries for the same thing), this decorates specific generated operations with the ergonomics a
/// workflow author expects: dropdowns that list your real recordings and formulas, binary file in and out,
/// polling until an asynchronous formula run finishes, and accumulating the chat stream into one answer.
/// Everything not named here still works through the generated operation, so coverage never regresses.

export function key(resource: string, operation: string): string {
  return `${resource}.${operation}`;
}

/// Path parameter -> the loadOptions method that can populate it. Keyed by the owning resource as well as
/// the parameter, because "id" means a different thing on each resource.
const PARAM_DROPDOWNS: Record<string, string> = {
  // Any parameter explicitly naming another entity, on any resource.
  "*.recordingId": "getRecordings",
  "*.formulaId": "getFormulas",
  "*.sectionId": "getFolders",
  "*.roomId": "getRooms",
  // A resource's own bare id.
  "recordings.id": "getRecordings",
  "formulas.id": "getFormulas",
  "sections.id": "getFolders",
  "rooms.id": "getRooms",
  "speakerProfiles.id": "getSpeakerProfiles",
  "meetingTypes.id": "getMeetingTypes",
};

/// Returns the loadOptions method for a path parameter, or undefined to leave it a plain text field.
/// Deliberately conservative: a segment or attachment id has no listing endpoint to populate from, and
/// guessing would produce an empty dropdown that looks broken.
export function loadOptionsFor(resource: string, param: string): string | undefined {
  return PARAM_DROPDOWNS[`${resource}.${param}`] ?? PARAM_DROPDOWNS[`*.${param}`];
}

export interface BinaryDownload {
  /// Default filename when the response carries no Content-Disposition.
  fileName: string;
}

/// Operations whose response is a file rather than JSON. They write to a binary property instead of `json`.
export const BINARY_DOWNLOADS: Record<string, BinaryDownload> = {
  "recordings.downloadTheTranscriptAsPlainText": { fileName: "transcript.txt" },
  "recordings.downloadTheTranscriptAsMarkdown": { fileName: "transcript.md" },
  "recordings.downloadTheTranscriptAsRtf": { fileName: "transcript.rtf" },
  "recordings.downloadTheTranscriptAsSubtitles": { fileName: "transcript.srt" },
  "recordings.streamOrDownloadTheAudio": { fileName: "recording.webm" },
  "attachments.downloadAnAttachment": { fileName: "attachment" },
  "formulaResults.downloadAFormulaDocument": { fileName: "document.md" },
};

export interface BinaryUpload {
  /// The multipart form field the controller binds the file to.
  field: string;
  /// Form fields always sent with the file.
  fixedFields?: Record<string, string>;
  /// Optional form fields the user may set.
  optionalFields?: { name: string; displayName: string; description: string }[];
}

/// Operations that take a multipart upload, so the file comes from an n8n binary property.
export const BINARY_UPLOADS: Record<string, BinaryUpload> = {
  "recordings.uploadARecording": {
    field: "audio",
    // Uploads carry no client-measured duration: the worker measures it and backfills it, so 0 is correct.
    fixedFields: { source: "Upload", durationMs: "0" },
    optionalFields: [
      { name: "title", displayName: "Title", description: "Name for the recording" },
      { name: "sectionId", displayName: "Folder ID", description: "Folder to file the recording in" },
      { name: "roomId", displayName: "Room ID", description: "Room to upload the recording into" },
    ],
  },
  "attachments.attachAFile": { field: "file" },
};

export interface WaitOperation {
  /// Where to poll for the result, templated with the run response's own fields.
  pollPath: string;
  /// Response field holding the terminal status.
  statusField: string;
  readyValue: string;
  failedValue: string;
}

/// Asynchronous operations that answer 202 and need polling for the finished output.
export const WAIT_OPERATIONS: Record<string, WaitOperation> = {
  "formulas.runAFormulaOverARecording": {
    pollPath: "/api/recordings/{recordingId}/formula-results/{id}",
    statusField: "status",
    readyValue: "Ready",
    failedValue: "Failed",
  },
};

/// Operations that answer with text/event-stream and must be accumulated into a single result.
export const SSE_OPERATIONS: string[] = ["chat.askAQuestionAndStreamTheAnswer"];
