import { test } from "node:test";
import assert from "node:assert/strict";
import {
  BINARY_DOWNLOADS,
  BINARY_UPLOADS,
  loadOptionsFor,
  SSE_OPERATIONS,
  WAIT_OPERATIONS,
  key,
} from "../nodes/Diariz/enhancements";
import GENERATED from "../nodes/Diariz/generated";

const operationExists = (k: string) => {
  const [resource, operation] = k.split(".");
  return GENERATED.some((r) => r.value === resource && r.operations.some((o) => o.value === operation));
};

test("offers a recording dropdown wherever a recording is identified", () => {
  assert.equal(loadOptionsFor("recordings", "id"), "getRecordings");
  assert.equal(loadOptionsFor("formulaResults", "recordingId"), "getRecordings");
  assert.equal(loadOptionsFor("attachments", "recordingId"), "getRecordings");
});

test("offers the right dropdown for each owning resource's own id", () => {
  assert.equal(loadOptionsFor("formulas", "id"), "getFormulas");
  assert.equal(loadOptionsFor("sections", "id"), "getFolders");
  assert.equal(loadOptionsFor("rooms", "id"), "getRooms");
  assert.equal(loadOptionsFor("speakerProfiles", "id"), "getSpeakerProfiles");
});

test("does not guess a dropdown for an unrelated id", () => {
  // A segment id or an attachment id has no listing endpoint to populate from, so it stays a plain field.
  assert.equal(loadOptionsFor("recordings", "segmentId"), undefined);
  assert.equal(loadOptionsFor("attachments", "attachmentId"), undefined);
  assert.equal(loadOptionsFor("chat", "id"), undefined);
});

test("every binary download names a real operation", () => {
  for (const k of Object.keys(BINARY_DOWNLOADS)) {
    assert.ok(operationExists(k), `${k} is not a generated operation`);
  }
});

test("every binary upload names a real operation and a form field", () => {
  for (const [k, upload] of Object.entries(BINARY_UPLOADS)) {
    assert.ok(operationExists(k), `${k} is not a generated operation`);
    assert.ok(upload.field.length > 0);
  }
});

test("covers the transcript exports, the audio, attachments and formula documents", () => {
  for (const k of [
    "recordings.downloadTheTranscriptAsPlainText",
    "recordings.downloadTheTranscriptAsMarkdown",
    "recordings.downloadTheTranscriptAsRtf",
    "recordings.downloadTheTranscriptAsSubtitles",
    "recordings.streamOrDownloadTheAudio",
    "attachments.downloadAnAttachment",
    "formulaResults.downloadAFormulaDocument",
  ]) {
    assert.ok(BINARY_DOWNLOADS[k], `${k} should produce binary output`);
  }
});

test("uploads a recording as multipart with the fields the API requires", () => {
  const upload = BINARY_UPLOADS["recordings.uploadARecording"];
  assert.ok(upload);
  assert.equal(upload.field, "audio");
  // The worker measures the true duration for uploads and backfills it, so 0 is correct here.
  assert.equal(upload.fixedFields?.durationMs, "0");
  assert.equal(upload.fixedFields?.source, "Upload");
});

test("attaches a file under the field name the controller binds", () => {
  assert.equal(BINARY_UPLOADS["attachments.attachAFile"]?.field, "file");
});

test("waits on the formula run, which answers 202 with a generating document", () => {
  const wait = WAIT_OPERATIONS["formulas.runAFormulaOverARecording"];
  assert.ok(wait);
  assert.equal(wait.pollPath, "/api/recordings/{recordingId}/formula-results/{id}");
  assert.ok(operationExists("formulas.runAFormulaOverARecording"));
});

test("accumulates the chat stream rather than returning raw SSE", () => {
  assert.ok(SSE_OPERATIONS.includes("chat.askAQuestionAndStreamTheAnswer"));
  assert.ok(operationExists("chat.askAQuestionAndStreamTheAnswer"));
});

test("builds a stable enhancement key", () => {
  assert.equal(key("recordings", "listRecordings"), "recordings.listRecordings");
});

test("detects array responses so Return All is offered where it makes sense", () => {
  const recordings = GENERATED.find((r) => r.value === "recordings")!;
  assert.equal(recordings.operations.find((o) => o.value === "listRecordings")!.returnsArray, true);
  assert.equal(recordings.operations.find((o) => o.value === "getARecording")!.returnsArray, false);
  // Search returns a structured object, not an array, so it must not offer Return All.
  const search = GENERATED.find((r) => r.value === "search")!;
  assert.equal(search.operations[0].returnsArray, false);
});
