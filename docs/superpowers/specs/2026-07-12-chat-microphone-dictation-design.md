# Chat microphone dictation - design

**Date:** 2026-07-12
**Status:** Approved (design phase)
**Version target:** 0.126.0 (functional enhancement: Minor +1, Build -> 0, from 0.125.1)
**Deployment surface:** Server redeploy only (no desktop-shell files touched).

## Summary

Add a microphone toggle to the chat input row so a user can dictate a message. Speech is
transcribed near-real-time and **appended into the chat textarea** (not auto-sent) so the user
reviews/edits before pressing send. Two transcription engines are selected automatically per
environment:

- **Browser `SpeechRecognition`** where available (plain Chrome/Edge tab): instant, native
  pause detection, no backend.
- **Server fallback** everywhere else (the Electron desktop shell, Safari, Firefox): the browser
  chunks the mic audio on each pause and POSTs each utterance to a new API endpoint, which forwards
  it to an OpenAI-compatible speech-to-text endpoint.

If neither engine is available (no `SpeechRecognition` **and** no server STT endpoint configured),
the mic button is not rendered - the feature is simply absent, nothing breaks.

## Decisions (locked)

1. **Engine:** browser API primary, server fallback (both).
2. **Mic source:** reuse the recorder's saved mic (`diariz.recorder.source` in localStorage) for the
   server path. The browser `SpeechRecognition` API cannot select an input device, so on that path the
   OS-default mic is used - an accepted limitation.
3. **STT config scope:** **server-level only** - a new `Dictation` options section in `AppOptions.cs`
   (env-driven). No new `UserSettings` columns, no EF migration, no settings UI. (Per-user config is a
   possible later enhancement - YAGNI for v1.)
4. **Dictation target:** appended into the input **textarea**; the user edits and sends manually.

## UI (ChatPanel.tsx)

The input row becomes: `[ textarea ] [ mic button ] [ send button ]` - mic **before** send, per the
request.

- **Send button** loses its "Send" text and becomes a **paper-plane send icon** (same button padding,
  so the panel width is unchanged). The existing streaming **Stop** button is unchanged.
- **Mic button** toggles between a **microphone** icon (idle) and a **stop (filled square)** icon
  (listening). It is:
  - Hidden when no engine is available.
  - Disabled while a chat reply is streaming (`streaming === true`).
  - Given an `aria-label`/`title` that flips between "Dictate message" and "Stop dictation".
- A subtle **listening indicator** (a pulsing dot, or reuse of the level-meter idea) shows while
  dictating. Keep it lightweight - no 60fps React state churn (write via ref, like `InputLevelMeter`).
- New i18n keys in `chat.json` for en/de/es/fr. **No em/en dashes** in any user-facing string.

## Architecture

Follows the codebase convention of a **pure, unit-tested module** + a **thin impure adapter** +
**thin component wiring** (mirrors `audioDevices.ts` / `recorderState.js`).

### `apps/web/src/lib/dictation.ts` (pure, unit-tested)

No browser APIs. Owns:

- `DictationStatus = "idle" | "starting" | "listening" | "error"`.
- Engine selection: `pickDictationEngine(env): "speech" | "server" | "none"` given a small capability
  object (`{ hasSpeechRecognition: boolean; hasServerStt: boolean }`) - `speech` wins when present,
  else `server` when a server endpoint is configured, else `none`.
- Text merge helper: given the committed input value, a just-finalized transcript, and the current
  interim tail, compute the textarea value. Handles spacing/capitalisation join rules (append with a
  single separating space, no leading space when the box is empty).

### `apps/web/src/lib/dictationEngine.ts` (impure adapter)

- Interface: `DictationEngine { start(cb): Promise<void>; stop(): void }` where
  `cb = { onInterim(text), onFinal(text), onError(err) }`.
- **`SpeechRecognitionEngine`**: wraps `webkitSpeechRecognition`/`SpeechRecognition` with
  `continuous = true`, `interimResults = true`. Each result -> `onInterim` (not final) or `onFinal`
  (isFinal). Restarts on transient `no-speech`/`network` end while still listening.
- **`ServerDictationEngine`**:
  - Captures the mic via the **existing** `getStream(resolvePersistedSource(saved, devices), constraints)`
    (reusing `audioSource.ts` / `audioDevices.ts` and the persisted recorder source + constraints).
  - Runs a Web Audio `AnalyserNode` and reuses `lib/audioLevel.ts` (`rms`, `normalizeLevel`,
    `nextSilenceMs`) to detect **speech-then-silence** = an utterance boundary.
  - Records with `MediaRecorder` (audio/webm). On a detected pause with non-trivial captured audio,
    finalizes the current chunk, POSTs it to `POST /api/chat/transcribe`, calls `onFinal` with the
    returned text, and immediately begins the next chunk. Interim UI is optional here (server latency
    means it is coarser than the browser path); at minimum show the "listening" indicator.
  - Tears down the stream + AudioContext on `stop()`.

### `useDictation` hook (in ChatPanel or a small hook file)

Owns the `DictationStatus`, instantiates the selected engine, and connects `onFinal`/`onInterim` to
`setInput`. `onFinal` commits into `input`; `onInterim` shows a live tail that is replaced on the next
final. Errors surface via the existing chat `error` state (localized message).

## Server (new)

### Options - `AppOptions.cs`

```csharp
/// <summary>OpenAI-compatible speech-to-text endpoint used for chat voice dictation (the server
/// fallback path, e.g. the Electron desktop shell where the browser SpeechRecognition API is
/// unavailable). Empty ApiBase disables the server path (the browser API is still used where present).</summary>
public class DictationOptions
{
    public const string Section = "Dictation";
    public string ApiBase { get; set; } = ""; // e.g. http://whisper:8000/v1 ; empty disables the server path
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "whisper-1";
    public int TimeoutSeconds { get; set; } = 30;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);
}
```

Registered in `Program.cs` like the other options sections.

### Client - `Services/DictationClient.cs`

Mirrors `SummarizationClient`: an `IDictationClient` with

```csharp
Task<string> TranscribeAsync(Stream audio, string contentType, string fileName, CancellationToken ct);
```

that POSTs `multipart/form-data` (`file` + `model`) to `{ApiBase}/audio/transcriptions`, sets a Bearer
key when configured, applies `TimeoutSeconds` via a linked CTS, and parses the OpenAI response
(`{ "text": "..." }`). Registered as a typed `HttpClient`.

### Endpoint - `POST /api/chat/transcribe`

- JWT-authenticated (like every user-facing endpoint), owner-scoped (no persistence, but auth-gated).
- Accepts a single audio part (multipart). Enforces a small size cap (a dictation utterance, e.g.
  <= a few MB) to avoid abuse - reuse/extend the audio-format guard only loosely (webm/opus is what the
  browser sends; the STT endpoint does its own decoding, so we don't need full magic-byte sniffing, but
  a content-type + size check is prudent).
- Returns 400 when `DictationOptions.Enabled` is false (server path not configured).
- On success returns `{ "text": "..." }`.

A capability flag is exposed to the web so it knows whether the server path exists. Simplest: extend an
existing bootstrap/settings payload the chat already fetches (`api.getUserSettings` -> add
`dictationServerAvailable: bool`), rather than a new endpoint. The web's `pickDictationEngine` reads it.

## Testing (TDD - required)

Write the failing test first in each case.

- **`dictation.test.ts`** (vitest): engine selection across capability combinations; the text-merge
  helper (empty box, trailing space, interim replacement); status transitions.
- **.NET** (`Diariz.Api.Tests`): `DictationClient` against a fake `HttpMessageHandler` (à la the
  summarization client tests) - verifies the multipart body carries the model + file, the Bearer header
  is set when a key is configured and omitted otherwise, and the response text is parsed. The endpoint:
  returns 400 when unconfigured, 401 without JWT, and returns the transcribed text on success (fake
  client). No Docker needed (unit layer).
- The impure engine adapters (`SpeechRecognitionEngine`, `ServerDictationEngine`) are kept thin and are
  verified **live in the browser preview** (no React component-testing lib is wired in this repo).

## Version + docs (single release per PR)

- Bump `version.json` 0.125.1 -> **0.126.0** and mirror in `apps/web/package.json`,
  `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`.
- Add a `RELEASES[0]` entry in `apps/web/src/lib/releases.ts` (version/date/pr/headline/summary + an
  `added` bullet). `RELEASES[0].version` must equal `version.json`.
- Add a `CAPABILITIES` table row (About box). Add a disclaimer line if a new third-party STT dependency
  warrants it.
- Update README Features table + `docs/features.md` + `CAPABILITIES` in lockstep (voice dictation in
  chat).
- Update `docs/Overall_Synopsis_of_Platform.md`: new `/api/chat/transcribe` endpoint + the optional
  external OpenAI-compatible STT dependency and the `Dictation` config section. **No** `Data_Schema.md`
  change (no schema/storage change - dictation persists nothing).
- No `MaintenanceController.CurrentFormat` bump (no migration).

## Non-goals (v1)

- Per-user STT endpoint config / settings UI.
- Device selection on the browser `SpeechRecognition` path (API limitation).
- Persisting dictated audio or transcripts (the text goes into the input box only).
- Auto-send after dictation (explicitly rejected in favour of review-then-send).
