import { describe, it, expect, vi } from "vitest";
import { attachScreenshotToChat, onChatScreenshotAttached } from "./chatAttachments";

const shot = { recordingId: "rec-1", screenshotId: "shot-a" };

describe("chatAttachments", () => {
  it("delivers an attached capture to every subscriber", () => {
    const one = vi.fn();
    const two = vi.fn();
    const offOne = onChatScreenshotAttached(one);
    const offTwo = onChatScreenshotAttached(two);

    attachScreenshotToChat(shot);

    expect(one).toHaveBeenCalledWith(shot);
    expect(two).toHaveBeenCalledWith(shot);
    offOne();
    offTwo();
  });

  it("stops delivering once a subscriber unsubscribes", () => {
    const listener = vi.fn();
    const off = onChatScreenshotAttached(listener);

    off();
    attachScreenshotToChat(shot);

    expect(listener).not.toHaveBeenCalled();
  });

  /// The viewer can be open with the chat panel unmounted (a route that never renders it); publishing to
  /// nobody must be a silent no-op rather than a crash inside the click handler.
  it("is a no-op when nothing is listening", () => {
    expect(() => attachScreenshotToChat(shot)).not.toThrow();
  });
});
