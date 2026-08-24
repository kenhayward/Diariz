import { describe, it, expect, vi } from "vitest";
import {
  watchDesktopDownloads,
  downloadProgress,
  type DesktopDownloadEvent,
} from "./desktopDownloads";

/// A fake of the `window.diariz` bridge the Electron preload injects.
function fakeBridge() {
  const listeners: ((e: DesktopDownloadEvent) => void)[] = [];
  return {
    listeners,
    emit(e: DesktopDownloadEvent) {
      for (const l of [...listeners]) l(e);
    },
    onDownloadEvent(cb: (e: DesktopDownloadEvent) => void) {
      listeners.push(cb);
      return () => {
        const i = listeners.indexOf(cb);
        if (i >= 0) listeners.splice(i, 1);
      };
    },
  };
}

const event = (over: Partial<DesktopDownloadEvent> = {}): DesktopDownloadEvent => ({
  type: "progress",
  id: 1,
  url: "https://host/api/maintenance/backup?access_token=t",
  filename: "diariz-backup.zip",
  receivedBytes: 1,
  totalBytes: 2,
  ...over,
});

describe("watchDesktopDownloads", () => {
  it("is a no-op without a desktop bridge", () => {
    const onEvent = vi.fn();

    // The web app runs this unconditionally, so a browser reaching for a missing bridge would break it.
    const dispose = watchDesktopDownloads(undefined, () => true, onEvent);
    dispose();

    expect(onEvent).not.toHaveBeenCalled();
  });

  it("delivers events whose url the caller claims", () => {
    const bridge = fakeBridge();
    const onEvent = vi.fn();
    watchDesktopDownloads(bridge, (url) => url.includes("/api/maintenance/backup"), onEvent);

    bridge.emit(event());

    expect(onEvent).toHaveBeenCalledTimes(1);
    expect(onEvent.mock.calls[0][0].filename).toBe("diariz-backup.zip");
  });

  it("ignores downloads belonging to something else", () => {
    // The shell handler is generic - audio and transcript downloads come down the same channel.
    const bridge = fakeBridge();
    const onEvent = vi.fn();
    watchDesktopDownloads(bridge, (url) => url.includes("/api/maintenance/backup"), onEvent);

    bridge.emit(event({ url: "https://host/api/recordings/1/audio", filename: "meeting.webm" }));

    expect(onEvent).not.toHaveBeenCalled();
  });

  it("stops delivering once disposed", () => {
    const bridge = fakeBridge();
    const onEvent = vi.fn();
    const dispose = watchDesktopDownloads(bridge, () => true, onEvent);

    dispose();
    bridge.emit(event());

    expect(onEvent).not.toHaveBeenCalled();
    expect(bridge.listeners.length).toBe(0);
  });
});

describe("downloadProgress", () => {
  it("reports starting until bytes arrive", () => {
    // will-download fires before the Save-As dialog resolves, so zero received is a real state.
    expect(downloadProgress({ receivedBytes: 0, totalBytes: 2048 }).phase).toBe("starting");
  });

  it("reports a percentage of the total when the total is known", () => {
    const p = downloadProgress({ receivedBytes: 512, totalBytes: 1024 });

    expect(p.phase).toBe("progressing");
    expect(p.percent).toBe(50);
    expect(p.sizeText).toBe("1 KB");
  });

  it("reports how much has arrived when the total is unknown", () => {
    // No Content-Length: there is no percentage to give, so say what has landed instead of showing 0%.
    const p = downloadProgress({ receivedBytes: 2048, totalBytes: 0 });

    expect(p.phase).toBe("progressing");
    expect(p.percent).toBeNull();
    expect(p.sizeText).toBe("2 KB");
  });

  it("clamps at 100 when the received count overshoots the total", () => {
    expect(downloadProgress({ receivedBytes: 1100, totalBytes: 1000 }).percent).toBe(100);
  });
});
