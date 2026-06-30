import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

// A JWT-shaped token whose payload decodes to { sub: "u1" } (used for the per-user pending key).
const TOKEN = `h.${btoa(JSON.stringify({ sub: "u1" }))}.s`;

vi.mock("../lib/api", () => ({
  api: { upload: vi.fn() },
  apiErrorMessage: (_e: unknown, fb: string) => fb,
  getToken: () => TOKEN,
}));
vi.mock("../lib/uploadContext", () => ({ useUpload: () => ({ uploadFiles: vi.fn() }) }));
vi.mock("../lib/audioSource", () => ({
  getStream: vi.fn(),
  isElectron: false,
  describeAudioError: () => "audio error",
}));
vi.mock("../lib/pendingRecording", () => ({
  savePendingRecording: vi.fn().mockResolvedValue(undefined),
  loadPendingRecording: vi.fn().mockResolvedValue(null),
  clearPendingRecording: vi.fn().mockResolvedValue(undefined),
}));

import { api } from "../lib/api";
import { loadPendingRecording, clearPendingRecording } from "../lib/pendingRecording";
import Recorder from "./Recorder";

const pending = {
  userId: "u1",
  blob: new Blob(["audio"], { type: "audio/webm" }),
  title: "Mic 6/30/2026",
  durationMs: 2_700_000,
  source: "Microphone" as const,
  createdAt: Date.now(),
};

describe("Recorder recovery", () => {
  beforeEach(() => vi.clearAllMocks());

  it("offers an unsaved recording and uploads it on demand", async () => {
    (loadPendingRecording as Mock).mockResolvedValue(pending);
    (api.upload as Mock).mockResolvedValue({});
    const onUploaded = vi.fn();

    render(<Recorder onUploaded={onUploaded} />);

    const uploadBtn = await screen.findByRole("button", { name: /upload now/i });
    fireEvent.click(uploadBtn);

    await waitFor(() =>
      expect(api.upload).toHaveBeenCalledWith(pending.blob, pending.title, pending.durationMs, "Microphone"),
    );
    await waitFor(() => expect(clearPendingRecording).toHaveBeenCalledWith("u1"));
    expect(onUploaded).toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("button", { name: /upload now/i })).toBeNull());
  });

  it("discards an unsaved recording after confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (loadPendingRecording as Mock).mockResolvedValue(pending);

    render(<Recorder onUploaded={() => {}} />);

    fireEvent.click(await screen.findByRole("button", { name: /discard/i }));

    await waitFor(() => expect(clearPendingRecording).toHaveBeenCalledWith("u1"));
    expect(api.upload).not.toHaveBeenCalled();
  });

  it("shows nothing to recover when there is no pending recording", async () => {
    (loadPendingRecording as Mock).mockResolvedValue(null);
    render(<Recorder onUploaded={() => {}} />);
    await screen.findByRole("button", { name: /record/i });
    expect(screen.queryByRole("button", { name: /upload now/i })).toBeNull();
  });
});
