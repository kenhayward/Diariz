/// The one-way channel that lets any part of the app hand a screen capture to the chat composer.
///
/// The chat panel and the screenshot viewer are siblings, not ancestor and descendant: the panel is
/// mounted by `Workspace`, the viewer by the routed recording page underneath it. Dragging a thumbnail
/// onto the composer needs no wiring because the browser carries the payload; the viewer's toolbar button
/// has no such transport, and threading a callback from `Workspace` down through the router `Outlet` to a
/// modal would put chat plumbing in every page in between.
///
/// So this is a module-level subscriber set: a plain in-tab pub/sub, deliberately NOT a BroadcastChannel
/// (unlike notesChannel, which spans two windows) - a capture is attached to the composer in the tab the
/// user clicked in. Publishing to nobody is a silent no-op, so a viewer opened where no chat panel is
/// mounted simply does nothing rather than throwing inside a click handler.
///
/// Two subscribers exist today and both are wanted: `ChatPanel` adds the capture to its tray, and
/// `Workspace` expands the chat panel if it was collapsed (the tray is useless behind a collapsed rail).

import type { ChatScreenshotRef } from "./types";

type Listener = (shot: ChatScreenshotRef) => void;

const listeners = new Set<Listener>();

/// Hand a capture to the chat composer. Callers pass only the identifying pair - the composer keeps the
/// tray's dedupe rule, so re-attaching one already there is a no-op rather than a duplicate thumbnail.
export function attachScreenshotToChat(shot: ChatScreenshotRef): void {
  // Iterate a copy: a listener that unsubscribes (or subscribes) while being notified must not mutate
  // the set mid-iteration.
  for (const listener of [...listeners]) listener(shot);
}

/// Subscribe to captures attached from elsewhere in the app. Returns the unsubscribe function, so an
/// effect can return it directly.
export function onChatScreenshotAttached(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/// Text extracted from a capture, on its way to the chat composer's context pill.
export interface ChatTextAttachment {
  name: string;
  text: string;
}

type TextListener = (attachment: ChatTextAttachment) => void;

const textListeners = new Set<TextListener>();

/// Hand extracted text to the chat composer.
///
/// A SECOND channel rather than a variant of the one above, deliberately: the two carry different things
/// to different places. A capture attached as an image rides to a vision model as pixels and lands in the
/// screenshot tray; extracted text lands in the context pill and is read by whatever model is answering.
/// Merging them would mean every subscriber had to discriminate, and a mistake there would silently turn
/// a vision attachment into an OCR pill (or the reverse).
export function attachTextToChat(attachment: ChatTextAttachment): void {
  // Iterate a copy, for the same reason as above: a listener may (un)subscribe while being notified.
  for (const listener of [...textListeners]) listener(attachment);
}

/// Subscribe to text extracted elsewhere in the app. Returns the unsubscribe function.
export function onChatTextAttached(listener: TextListener): () => void {
  textListeners.add(listener);
  return () => {
    textListeners.delete(listener);
  };
}

type LiveRecordingListener = (recordingId: string) => void;

const liveRecordingListeners = new Set<LiveRecordingListener>();

/// Put the meeting currently being recorded into the chat prompt as sticky context.
///
/// A THIRD channel, on the same reasoning that separates the two above: it carries a different thing to
/// a different place. What crosses here is a recording **id**, not text - and that is the whole design.
///
/// The server already frames a recording in status `Live` with "This meeting is IN PROGRESS and still
/// being recorded" (`ChatContextBuilder`), and a live session creates the recording from the first
/// second, so an id is resolved against the transcript as it stands *at the moment the question is
/// asked*. A pasted snapshot would be stale the moment the meeting carried on, and it would also have to
/// go somewhere: the composer's context pill is single-origin, so a transcript pasted into it would
/// either accumulate a second copy on every press or fight the OCR/file pill with a confirm dialog.
export function attachLiveRecordingToChat(recordingId: string): void {
  // Iterate a copy, for the same reason as the channels above.
  for (const listener of [...liveRecordingListeners]) listener(recordingId);
}

/// Subscribe to the running meeting being sent to chat. Returns the unsubscribe function.
export function onChatLiveRecordingAttached(listener: LiveRecordingListener): () => void {
  liveRecordingListeners.add(listener);
  return () => {
    liveRecordingListeners.delete(listener);
  };
}

const liveRecordingEndedListeners = new Set<LiveRecordingListener>();

/// The recording named here has stopped, so anything presenting it as live should stop saying so.
///
/// Its own channel rather than an `attach(null)`, because the two are different events: attaching is
/// the user asking for something, and this is the world changing underneath what they asked for. A
/// subscriber that only cares about one of them should not have to discriminate.
///
/// The id is carried so a stop cannot clear a pill for a *different* meeting - stopping one recording
/// and starting another is an ordinary thing to do, and the second one's pill must survive the first
/// one's stop arriving late.
export function detachLiveRecordingFromChat(recordingId: string): void {
  for (const listener of [...liveRecordingEndedListeners]) listener(recordingId);
}

/// Subscribe to the running meeting ending. Returns the unsubscribe function.
export function onChatLiveRecordingDetached(listener: LiveRecordingListener): () => void {
  liveRecordingEndedListeners.add(listener);
  return () => {
    liveRecordingEndedListeners.delete(listener);
  };
}
