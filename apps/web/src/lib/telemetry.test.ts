import { describe, it, expect } from "vitest";
import { isSensitiveKey, stripQueryString, scrubUrlsIn, REDACTED } from "./telemetry";

describe("isSensitiveKey", () => {
  it.each(["Authorization", "cookie", "password", "apiKey", "access_key", "accessKey", "token", "secret"])(
    "treats %s as a credential",
    (key) => expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["text", "transcript", "transcription", "segments", "words", "summary", "minutes", "note", "notes", "content"])(
    "treats %s as meeting content",
    (key) => expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["embedding", "embeddings"])("treats %s as a biometric voiceprint", (key) =>
    expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["recordingId", "transcriptionId", "blobKey", "userId", "model", "language", "status"])(
    "keeps %s, which is needed to diagnose",
    (key) => expect(isSensitiveKey(key)).toBe(false),
  );
});

describe("stripQueryString", () => {
  it("removes the SignalR access token while keeping the path", () => {
    const stripped = stripQueryString("https://app.example/hubs/transcription?access_token=A_LIVE_JWT");
    expect(stripped).toBe("https://app.example/hubs/transcription");
    expect(stripped).not.toContain("A_LIVE_JWT");
  });

  it("removes a query string from a relative URL", () => {
    expect(stripQueryString("/hubs/transcription?access_token=A_LIVE_JWT")).toBe("/hubs/transcription");
  });

  it("leaves a URL with no query string untouched", () => {
    expect(stripQueryString("https://app.example/api/recordings")).toBe("https://app.example/api/recordings");
  });

  it("leaves a value that is not a URL untouched", () => {
    expect(stripQueryString("select * from recordings where id = ?")).toBe(
      "select * from recordings where id = ?",
    );
  });
});

describe("scrubUrlsIn", () => {
  it("strips the query from a URL embedded in a longer description", () => {
    // Sentry describes fetch spans as "<METHOD> <url>".
    expect(scrubUrlsIn("GET https://app.example/hubs/transcription?access_token=A_LIVE_JWT")).toBe(
      "GET https://app.example/hubs/transcription",
    );
  });

  it("leaves free text alone", () => {
    expect(scrubUrlsIn("Detail panel crashed")).toBe("Detail panel crashed");
  });
});

describe("REDACTED", () => {
  it("matches the marker the other two runtimes use", () => {
    expect(REDACTED).toBe("[redacted]");
  });
});
