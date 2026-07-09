import { describe, it, expect } from "vitest";
import { describeAudioError, supportsDisplayAudio } from "./audioSource";

const err = (name: string) => Object.assign(new Error(name), { name });

describe("describeAudioError", () => {
  it("explains a denied/blocked permission", () => {
    expect(describeAudioError(err("NotAllowedError"), "mic", false)).toMatch(/permission|blocked|allow/i);
  });

  it("explains the mic being held by another app (the usual 'worked before' cause)", () => {
    const msg = describeAudioError(err("NotReadableError"), "mic", false);
    expect(msg).toMatch(/another app|in use|unavailable/i);
  });

  it("explains a missing device", () => {
    expect(describeAudioError(err("NotFoundError"), "mic", false)).toMatch(/no microphone|not found|connected/i);
  });

  it("guides toward 'Share audio' for system capture rather than claiming desktop-only", () => {
    const msg = describeAudioError(err("NotAllowedError"), "system", false);
    expect(msg.toLowerCase()).not.toContain("needs the desktop app");
    expect(msg).toMatch(/share audio/i);
  });

  it("falls back to a generic message for unknown errors (in a secure context)", () => {
    // jsdom has no navigator.mediaDevices; stub it so we exercise the true generic path, not the
    // insecure-context branch.
    const original = Object.getOwnPropertyDescriptor(navigator, "mediaDevices");
    Object.defineProperty(navigator, "mediaDevices", { value: {}, configurable: true });
    try {
      expect(describeAudioError(err("WeirdError"), "mic", false)).toMatch(/could not access/i);
    } finally {
      if (original) Object.defineProperty(navigator, "mediaDevices", original);
      else delete (navigator as { mediaDevices?: unknown }).mediaDevices;
    }
  });

  it("flags an insecure context when the mediaDevices API is unavailable", () => {
    // jsdom already lacks navigator.mediaDevices, matching an http/non-localhost page.
    expect(describeAudioError(err("TypeError"), "mic", false)).toMatch(/secure page|localhost|https/i);
  });
});

describe("supportsDisplayAudio", () => {
  it("true only when getDisplayMedia exists (Chromium/desktop)", () => {
    expect(supportsDisplayAudio({ mediaDevices: { getDisplayMedia: () => {} } } as unknown as Navigator)).toBe(true);
    expect(supportsDisplayAudio({ mediaDevices: {} } as unknown as Navigator)).toBe(false);
    expect(supportsDisplayAudio({} as unknown as Navigator)).toBe(false);
  });
});
