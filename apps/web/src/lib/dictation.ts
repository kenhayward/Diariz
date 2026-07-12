// Pure helpers for chat voice dictation. No browser APIs here (mirrors audioDevices.ts / audioLevel.ts)
// so engine selection and text merging are unit-testable; dictationEngine.ts owns the impure adapters.

export type DictationStatus = "idle" | "starting" | "listening" | "error";
export type DictationEngineKind = "speech" | "server" | "none";

export interface DictationCapabilities {
  /** The browser exposes SpeechRecognition / webkitSpeechRecognition. */
  hasSpeechRecognition: boolean;
  /** The server has an OpenAI-compatible STT endpoint configured (the fallback path). */
  hasServerStt: boolean;
}

/** Choose the dictation engine: the instant browser API wins; else the server path; else nothing. */
export function pickDictationEngine(caps: DictationCapabilities): DictationEngineKind {
  if (caps.hasSpeechRecognition) return "speech";
  if (caps.hasServerStt) return "server";
  return "none";
}

/** Append a finalized transcript fragment to the current textarea value, joining with a single space
 * (unless the box is empty or already ends in whitespace). Blank fragments are ignored. */
export function appendTranscript(current: string, fragment: string): string {
  const frag = fragment.trim();
  if (!frag) return current;
  if (!current) return frag;
  return /\s$/.test(current) ? current + frag : current + " " + frag;
}
