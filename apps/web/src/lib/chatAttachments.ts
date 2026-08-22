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
