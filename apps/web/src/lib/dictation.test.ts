import { describe, it, expect } from "vitest";
import { pickDictationEngine, appendTranscript } from "./dictation";

describe("pickDictationEngine", () => {
  it("prefers the browser SpeechRecognition engine when available", () => {
    expect(pickDictationEngine({ hasSpeechRecognition: true, hasServerStt: true })).toBe("speech");
    expect(pickDictationEngine({ hasSpeechRecognition: true, hasServerStt: false })).toBe("speech");
  });

  it("falls back to the server engine when only the server STT endpoint exists", () => {
    expect(pickDictationEngine({ hasSpeechRecognition: false, hasServerStt: true })).toBe("server");
  });

  it("returns none when neither is available", () => {
    expect(pickDictationEngine({ hasSpeechRecognition: false, hasServerStt: false })).toBe("none");
  });
});

describe("appendTranscript", () => {
  it("returns the fragment when the box is empty", () => {
    expect(appendTranscript("", "hello")).toBe("hello");
  });

  it("joins with a single space when the box has no trailing whitespace", () => {
    expect(appendTranscript("hello", "world")).toBe("hello world");
  });

  it("does not add a second space when the box already ends in whitespace", () => {
    expect(appendTranscript("hello ", "world")).toBe("hello world");
    expect(appendTranscript("hello\n", "world")).toBe("hello\nworld");
  });

  it("trims the incoming fragment and ignores blank fragments", () => {
    expect(appendTranscript("hello", "  world  ")).toBe("hello world");
    expect(appendTranscript("hello", "   ")).toBe("hello");
  });
});
