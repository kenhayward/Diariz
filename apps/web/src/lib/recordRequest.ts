/// A one-line channel for asking the recorder to start, from somewhere that is not the recorder.
///
/// The recorder lives in the top bar and owns the microphone, the clock and the current audio-source choice.
/// The Join-the-meeting button lives in a routed page. Rather than lift all of that state up, this mirrors
/// what already exists for the tray (`trayRecorder.ts`): a request goes out, the recorder answers it with the
/// settings the user has already chosen on screen.
///
/// Deliberately not React context - the recorder is mounted once, high in the tree, and a plain subscription
/// keeps the page that asks entirely ignorant of where the recorder is.

type Listener = () => void;

const listeners = new Set<Listener>();

/// Ask the recorder to start with the user's current settings. A no-op when nothing is listening (no recorder
/// mounted, e.g. in a test rendering the page alone), which is the right answer rather than an error.
export function requestRecording(): void {
  for (const listener of [...listeners]) listener();
}

/// Subscribe to start requests. Returns an unsubscribe function.
export function onRecordingRequested(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
