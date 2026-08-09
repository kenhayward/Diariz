/// A one-line channel for asking the recorder to start, from somewhere that is not the recorder.
///
/// The recorder lives in the top bar and owns the microphone, the clock and the current audio-source choice.
/// The Join-the-meeting button lives in a routed page. Rather than lift all of that state up, this mirrors
/// what already exists for the tray (`trayRecorder.ts`): a request goes out, the recorder answers it with the
/// settings the user has already chosen on screen.
///
/// Deliberately not React context - the recorder is mounted once, high in the tree, and a plain subscription
/// keeps the page that asks entirely ignorant of where the recorder is.

/// The calendar event a recording was started from. Carried on the request because the recorder cannot look
/// it up: it knows nothing about routes or the calendar. Its end time drives the auto-stop, and its subject
/// names the recording.
export interface CalendarEventContext {
  /// The event's own id - carried for provenance and future linking; the recorder does not need it to stop.
  id: string;
  /// The invite's subject. Becomes the recording's name so it reads as the meeting, not "Recording 14:32".
  summary: string | null;
  /// The invite's end time (ISO). Null for an event without a usable one (e.g. all-day), which simply means
  /// no calendar-driven auto-stop.
  endsAt: string | null;
  /// Which calendar the event lives on. Passed straight through when linking so the server targets the right
  /// one (a user's meetings span primary, team, subscribed and mirrored calendars) rather than searching.
  calendarId: string | null;
}

export interface RecordingRequest {
  /// Set when the recording is being started from a calendar event.
  calendarEvent?: CalendarEventContext;
}

type Listener = (request: RecordingRequest) => void;

const listeners = new Set<Listener>();

/// Ask the recorder to start with the user's current settings. A no-op when nothing is listening (no recorder
/// mounted, e.g. in a test rendering the page alone), which is the right answer rather than an error.
///
/// If a recording is already running the recorder ends it first - the previous take is stopped and uploaded
/// on its own before this one begins, rather than being discarded or silently continued. Joining a second
/// meeting is an unambiguous "I have moved on".
export function requestRecording(request: RecordingRequest = {}): void {
  for (const listener of [...listeners]) listener(request);
}

/// Subscribe to start requests. Returns an unsubscribe function.
export function onRecordingRequested(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
