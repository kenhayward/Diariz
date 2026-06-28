import { describe, it, expect, vi } from "vitest";
import { runUploadBatch, type UploadItem } from "./uploadQueue";

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
    expect(items[1].error).toMatch(/unsupported/i); // .txt rejected by precheck
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
});
