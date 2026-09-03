import { describe, it, expect, vi } from "vitest";
import {
  attachLiveRecordingToChat,
  onChatLiveRecordingAttached,
  attachScreenshotToChat,
  attachTextToChat,
  onChatScreenshotAttached,
  onChatTextAttached,
} from "./chatAttachments";

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

describe("chatAttachments - extracted text", () => {
  it("delivers extracted text to every subscriber", () => {
    const one = vi.fn();
    const two = vi.fn();
    const offOne = onChatTextAttached(one);
    const offTwo = onChatTextAttached(two);

    attachTextToChat({ name: "Screenshot at 1:05 (OCR)", text: "Extracted" });

    expect(one).toHaveBeenCalledWith({ name: "Screenshot at 1:05 (OCR)", text: "Extracted" });
    expect(two).toHaveBeenCalledWith({ name: "Screenshot at 1:05 (OCR)", text: "Extracted" });
    offOne();
    offTwo();
  });

  it("stops delivering once a subscriber unsubscribes", () => {
    const listener = vi.fn();
    const off = onChatTextAttached(listener);
    off();

    attachTextToChat({ name: "n", text: "t" });

    expect(listener).not.toHaveBeenCalled();
  });

  /// Publishing where no chat panel is mounted is a no-op, not a throw inside a click handler.
  it("publishing to nobody does not throw", () => {
    expect(() => attachTextToChat({ name: "n", text: "t" })).not.toThrow();
  });

  /// The two channels are separate: a capture attached as an IMAGE must not arrive as text, or a
  /// vision-model attachment would silently become an OCR context pill.
  it("keeps the image and text channels apart", () => {
    const onText = vi.fn();
    const onShot = vi.fn();
    const offText = onChatTextAttached(onText);
    const offShot = onChatScreenshotAttached(onShot);

    attachScreenshotToChat(shot);
    expect(onText).not.toHaveBeenCalled();

    attachTextToChat({ name: "n", text: "t" });
    expect(onShot).toHaveBeenCalledTimes(1);

    offText();
    offShot();
  });

  /// A THIRD channel, for the same reason the text one is separate from the image one: it carries an
  /// entirely different thing to a different place. A capture rides as pixels into the screenshot tray;
  /// extracted text lands in the context pill; a live recording is neither - it is an id the SERVER
  /// resolves, so the transcript the model reads is always current rather than a paste that went stale
  /// the moment the meeting carried on.
  it("carries a live recording id to whoever is listening", () => {
    const on = vi.fn();
    const off = onChatLiveRecordingAttached(on);

    attachLiveRecordingToChat("rec-live");

    expect(on).toHaveBeenCalledWith("rec-live");
    off();
  });

  it("stops delivering once unsubscribed", () => {
    const on = vi.fn();
    onChatLiveRecordingAttached(on)();

    attachLiveRecordingToChat("rec-live");

    expect(on).not.toHaveBeenCalled();
  });

  it("publishing a live recording to nobody does not throw", () => {
    expect(() => attachLiveRecordingToChat("rec-live")).not.toThrow();
  });

  it("keeps the live-recording channel apart from the other two", () => {
    const onText = vi.fn();
    const onShot = vi.fn();
    const onLive = vi.fn();
    const offs = [onChatTextAttached(onText), onChatScreenshotAttached(onShot), onChatLiveRecordingAttached(onLive)];

    attachLiveRecordingToChat("rec-live");

    expect(onText).not.toHaveBeenCalled();
    expect(onShot).not.toHaveBeenCalled();
    expect(onLive).toHaveBeenCalledTimes(1);
    offs.forEach((off) => off());
  });
});
