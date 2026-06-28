import { precheckUpload } from "./audioFormats";

// Pure orchestration for uploading a batch of files one at a time, reporting per-file status as it goes.
// Tolerant of partial failures: a rejected file (bad type / too big / server error) is marked failed and
// the rest of the batch still runs. The React layer supplies `upload` (which should throw a clean Error)
// and renders the status list from `onUpdate`.

export type UploadItemStatus = "queued" | "uploading" | "done" | "failed";

export interface UploadItem {
  id: string;
  name: string;
  status: UploadItemStatus;
  error?: string;
}

export interface RunUploadBatchDeps {
  upload: (file: File) => Promise<void>;
  onUpdate: (items: UploadItem[]) => void;
  /// Called after each successful upload (e.g. refresh the recordings list).
  onSuccess?: () => void;
  /// Per-file client pre-check; returns an error message or null. Defaults to the audio pre-check.
  precheck?: (file: File) => string | null;
}

export async function runUploadBatch(files: File[], deps: RunUploadBatchDeps): Promise<UploadItem[]> {
  const precheck = deps.precheck ?? precheckUpload;
  const items: UploadItem[] = files.map((f, i) => ({ id: `${i}-${f.name}`, name: f.name, status: "queued" }));
  deps.onUpdate(items.map((i) => ({ ...i })));

  for (let i = 0; i < files.length; i++) {
    const problem = precheck(files[i]);
    if (problem) {
      items[i] = { ...items[i], status: "failed", error: problem };
      deps.onUpdate(items.map((x) => ({ ...x })));
      continue;
    }

    items[i] = { ...items[i], status: "uploading" };
    deps.onUpdate(items.map((x) => ({ ...x })));

    try {
      await deps.upload(files[i]);
      items[i] = { ...items[i], status: "done" };
      deps.onSuccess?.();
    } catch (e) {
      items[i] = { ...items[i], status: "failed", error: e instanceof Error ? e.message : "Upload failed." };
    }
    deps.onUpdate(items.map((x) => ({ ...x })));
  }

  return items;
}
