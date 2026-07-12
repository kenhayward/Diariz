// Impure dictation engine adapters. Two implementations share one callback shape; ChatPanel picks between
// them via pickDictationEngine (dictation.ts). Verified live in the browser (no component-test lib here).

import { getStream, listInputDevices } from "./audioSource";
import { resolvePersistedSource, DEFAULT_CONSTRAINTS } from "./audioDevices";
import type { AudioConstraints, PersistedSource, SourceSelection } from "./audioDevices";
import { rms, normalizeLevel, nextSilenceMs, SILENCE_LEVEL } from "./audioLevel";

export interface DictationCallbacks {
  /** Live, not-yet-final text (browser path only) - shown as a preview, replaced on the next final/interim. */
  onInterim(text: string): void;
  /** A finalized utterance - committed into the input box. */
  onFinal(text: string): void;
  /** A fatal error; the engine has stopped. */
  onError(message: string): void;
}

export interface DictationEngine {
  start(cb: DictationCallbacks): Promise<void>;
  stop(): void;
}

/** Whether the browser exposes a *working* SpeechRecognition API. Electron (the desktop shell) exposes the
 * constructor but every call fails there (no bundled Google backend), so we treat it as absent and fall back
 * to the server path - the whole point of having a server engine for the desktop app. */
export function hasSpeechRecognition(win: Window = window): boolean {
  const w = win as unknown as {
    SpeechRecognition?: unknown;
    webkitSpeechRecognition?: unknown;
    diariz?: { isElectron?: boolean };
  };
  if (w.diariz?.isElectron) return false;
  return Boolean(w.SpeechRecognition || w.webkitSpeechRecognition);
}

// ---- Browser SpeechRecognition engine ----

/* eslint-disable @typescript-eslint/no-explicit-any */
export function createSpeechEngine(win: Window = window): DictationEngine {
  const Ctor: any =
    (win as any).SpeechRecognition ?? (win as any).webkitSpeechRecognition;
  let rec: any = null;
  let stopped = false;

  return {
    async start(cb) {
      stopped = false;
      rec = new Ctor();
      rec.continuous = true;
      rec.interimResults = true;
      rec.onresult = (e: any) => {
        let interim = "";
        for (let i = e.resultIndex; i < e.results.length; i++) {
          const r = e.results[i];
          if (r.isFinal) cb.onFinal(r[0].transcript);
          else interim += r[0].transcript;
        }
        cb.onInterim(interim);
      };
      rec.onerror = (e: any) => {
        // "no-speech"/"aborted" are transient; surface anything else.
        if (e.error && e.error !== "no-speech" && e.error !== "aborted") cb.onError(String(e.error));
      };
      rec.onend = () => {
        // The API stops itself after a pause; keep listening until the user stops.
        if (!stopped) {
          try { rec.start(); } catch { /* already starting */ }
        }
      };
      rec.start();
    },
    stop() {
      stopped = true;
      try { rec?.stop(); } catch { /* not running */ }
      rec = null;
    },
  };
}
/* eslint-enable @typescript-eslint/no-explicit-any */

// ---- Server-chunking engine ----

const SOURCE_KEY = "diariz.recorder.source";
const CONSTRAINTS_KEY = "diariz.recorder.audioConstraints";
// A speech->silence gap this long marks an utterance boundary (much shorter than the recorder's 15s hint).
const PAUSE_MS = 800;
// Ignore chunks with less than this much cumulative speech (avoids sending pure noise/clicks).
const MIN_SPEECH_MS = 300;

function loadSavedSource(): PersistedSource | null {
  try {
    const raw = localStorage.getItem(SOURCE_KEY);
    return raw ? (JSON.parse(raw) as PersistedSource) : null;
  } catch {
    return null;
  }
}

function loadSavedConstraints(): AudioConstraints {
  try {
    const raw = localStorage.getItem(CONSTRAINTS_KEY);
    return raw ? { ...DEFAULT_CONSTRAINTS, ...(JSON.parse(raw) as Partial<AudioConstraints>) } : DEFAULT_CONSTRAINTS;
  } catch {
    return DEFAULT_CONSTRAINTS;
  }
}

/** Resolve the recorder's saved mic selection against the currently-available devices. */
async function resolveSavedSelection(): Promise<SourceSelection> {
  const saved = loadSavedSource();
  const { devices } = await listInputDevices().catch(() => ({ devices: [], hasLabels: false }));
  // "none" (system-only) makes no sense for dictation; fall back to the default mic.
  const sel = resolvePersistedSource(saved, devices);
  return sel.kind === "none" ? { kind: "default" } : sel;
}

/**
 * Server dictation: capture the recorder's saved mic, watch the input level, and each time speech is
 * followed by a >=PAUSE_MS silence, finalize the current MediaRecorder into a self-contained webm and
 * POST it via `transcribe`. Restarting the recorder per utterance keeps each blob independently decodable.
 */
export function createServerEngine(
  transcribe: (blob: Blob) => Promise<string>,
): DictationEngine {
  let session: { stream: MediaStream; stop: () => void } | null = null;
  let ctx: AudioContext | null = null;
  let raf = 0;
  let recorder: MediaRecorder | null = null;
  let chunks: Blob[] = [];
  let stopped = false;
  let sending = false;

  function flushAndSend() {
    const r = recorder;
    if (!r || r.state === "inactive") return;
    // onstop assembles the blob and POSTs it, then restarts for the next utterance.
    r.stop();
  }

  return {
    async start(cb) {
      stopped = false;
      try {
        const selection = await resolveSavedSelection();
        session = await getStream(selection, loadSavedConstraints());
      } catch (e) {
        cb.onError((e as { message?: string })?.message ?? "Could not access the microphone.");
        return;
      }

      try {
        const stream = session.stream;
        const Ctx = (window as unknown as { AudioContext?: typeof AudioContext; webkitAudioContext?: typeof AudioContext })
          .AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
        if (!Ctx) {
          session.stop();
          session = null;
          cb.onError("Web Audio is unavailable.");
          return;
        }

        ctx = new Ctx();
        const analyser = ctx.createAnalyser();
        analyser.fftSize = 256;
        ctx.createMediaStreamSource(stream).connect(analyser);
        const buf = new Uint8Array(analyser.fftSize);

        let last: number | null = null;
        let silentMs = 0;
        let spokeMs = 0;

        const startRecorder = () => {
          chunks = [];
          recorder = new MediaRecorder(stream, { mimeType: "audio/webm" });
          recorder.ondataavailable = (e) => e.data.size > 0 && chunks.push(e.data);
          recorder.onstop = () => {
            const blob = new Blob(chunks, { type: "audio/webm" });
            if (!stopped) startRecorder(); // ready for the next utterance immediately
            if (!stopped && spokeMs >= MIN_SPEECH_MS && blob.size > 0 && !sending) {
              sending = true;
              transcribe(blob)
                .then((text) => { if (!stopped && text.trim()) cb.onFinal(text); })
                .catch((e) => cb.onError((e as { message?: string })?.message ?? "Transcription failed."))
                .finally(() => { sending = false; });
            }
            spokeMs = 0;
          };
          recorder.start(250);
        };
        startRecorder();

        const tick = (now: number) => {
          analyser.getByteTimeDomainData(buf);
          const level = normalizeLevel(rms(buf));
          const dt = last == null ? 0 : now - last;
          last = now;
          if (level >= SILENCE_LEVEL) { spokeMs += dt; silentMs = 0; }
          else { silentMs = nextSilenceMs(silentMs, level, dt); }
          // Boundary: we have real speech AND a sustained pause AND we're not mid-send.
          if (spokeMs >= MIN_SPEECH_MS && silentMs >= PAUSE_MS && !sending) {
            silentMs = 0;
            flushAndSend();
          }
          raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
      } catch (e) {
        cancelAnimationFrame(raf);
        try { recorder?.stop(); } catch { /* inactive */ }
        recorder = null;
        session?.stop();
        session = null;
        if (ctx) { void ctx.close(); ctx = null; }
        cb.onError((e as { message?: string })?.message ?? "Could not start dictation.");
      }
    },
    stop() {
      stopped = true;
      cancelAnimationFrame(raf);
      try { recorder?.stop(); } catch { /* inactive */ }
      recorder = null;
      session?.stop();
      session = null;
      if (ctx) { void ctx.close(); ctx = null; }
    },
  };
}
