import { describe, it, expect, vi } from "vitest";
import { runUploadBatch, type UploadItem, type UploadBatchHandle } from "./uploadQueue";

const file = (name: string, size = 100) => ({ name, size }) as unknown as File;

describe("runUploadBatch", () => {
  it("uploads valid files, skips invalid ones, and tolerates failures", async () => {
    const upload = vi.fn(async (f: File) => {
      if (f.name === "boom.wav") throw new Error("server said no");
    });
    const onSuccess = vi.fn();

    const items = await runUploadBatch(
      [file("good.mp3"), file("notes.txt"), file("boom.wav")],
      { upload, onUpdate: () => {}, onSuccess },
    );

    expect(items.map((i) => i.status)).toEqual(["done", "failed", "failed"]);
    expect(items[1].error).toMatch(/Unsupported file type/); // .txt rejected by the source check
    expect(items[2].error).toBe("server said no"); // upload threw
    expect(upload).toHaveBeenCalledTimes(2); // .txt never reaches the uploader
    expect(onSuccess).toHaveBeenCalledTimes(1); // one success
  });

  it("emits progressive status updates (queued -> uploading -> done)", async () => {
    const snapshots: UploadItem[][] = [];
    await runUploadBatch([file("a.wav")], {
      upload: async () => {},
      onUpdate: (items) => snapshots.push(items.map((i) => ({ ...i }))),
    });

    const statusesForA = snapshots.map((s) => s[0].status);
    expect(statusesForA[0]).toBe("queued");
    expect(statusesForA).toContain("uploading");
    expect(statusesForA[statusesForA.length - 1]).toBe("done");
  });

  it("does nothing for an empty list", async () => {
    const upload = vi.fn();
    const items = await runUploadBatch([], { upload, onUpdate: () => {} });
    expect(items).toEqual([]);
    expect(upload).not.toHaveBeenCalled();
  });

  it("extracts containers, passes audio straight through, and reports progress", async () => {
    const video = file("Town Hall.mp4", 2_800_000_000);
    const audio = file("memo.wav");
    const extracted = file("Town Hall.webm", 52_000_000);
    const extract = vi.fn(async (_f: File, o: { onProgress: (n: number) => void }) => {
      o.onProgress(0.5);
      return extracted;
    });
    const upload = vi.fn(async () => {});
    const snapshots: UploadItem[][] = [];

    const items = await runUploadBatch([video, audio], {
      upload,
      extract,
      onUpdate: (i) => snapshots.push(i.map((x) => ({ ...x }))),
    });

    expect(items.map((i) => i.status)).toEqual(["done", "done"]);
    expect(extract).toHaveBeenCalledTimes(1); // the .wav never reaches the extractor
    expect(upload.mock.calls[0][0]).toBe(extracted); // the video uploaded its EXTRACTED file
    expect(upload.mock.calls[1][0]).toBe(audio); // the .wav uploaded itself, untouched
    expect(snapshots.some((s) => s[0].status === "extracting")).toBe(true);
    expect(snapshots.some((s) => s[0].progress === 0.5)).toBe(true);
  });

  it("uploads a passed-through container without re-encoding it", async () => {
    const source = file("screen.webm", 1_000);
    // A container with no video track: the extractor hands the original File straight back.
    const extract = vi.fn(async (f: File) => f);
    const upload = vi.fn(async () => {});

    await runUploadBatch([source], { upload, extract, onUpdate: () => {} });

    expect(upload).toHaveBeenCalledWith(source);
  });

  it("marks a cancelled extraction cancelled and keeps going", async () => {
    let handle: UploadBatchHandle | undefined;
    const extract = vi.fn(
      (_f: File, o: { signal: AbortSignal }) =>
        new Promise<File>((_resolve, reject) => {
          o.signal.addEventListener("abort", () => reject(new Error("aborted")));
        }),
    );
    const upload = vi.fn(async () => {});

    const promise = runUploadBatch([file("a.mp4"), file("b.wav")], {
      upload,
      extract,
      onUpdate: () => {},
      onHandle: (h) => {
        handle = h;
      },
    });
    handle!.cancel("0-a.mp4");
    const items = await promise;

    expect(items.map((i) => i.status)).toEqual(["cancelled", "done"]);
    expect(upload).toHaveBeenCalledTimes(1); // the cancelled video never uploaded
  });

  it("fails an unextractable video without uploading it", async () => {
    const extract = vi.fn(async () => {
      throw new Error("That video has no audio track.");
    });
    const upload = vi.fn(async () => {});

    const items = await runUploadBatch([file("silent.mp4"), file("ok.mp3")], {
      upload,
      extract,
      onUpdate: () => {},
    });

    expect(items[0].status).toBe("failed");
    expect(items[0].error).toBe("That video has no audio track.");
    expect(upload).toHaveBeenCalledTimes(1); // never falls back to uploading the video
  });
});
