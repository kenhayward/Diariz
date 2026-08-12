import { classifyFile, resultProblem, sourceProblem, type MediaKind } from "./mediaKinds";

// Pure orchestration for uploading a batch of files one at a time, reporting per-file status as it
// goes. Tolerant of partial failures: a rejected file (bad type / too big / unextractable / server
// error) is marked failed and the rest of the batch still runs. The React layer supplies `upload` and
// `extract` (which should throw a clean Error) and renders the status list from `onUpdate`.
//
// Three stages, in this order, and the order is the point:
//   1. source  - type and a sanity ceiling, judged on the dropped file (which may be a multi-GB video)
//   2. extract - containers only; strips video, or hands the file straight back if it holds none
//   3. result  - the 500 MB upload cap, judged on what is actually going to be sent
// Judging the upload cap on the source rejected every real webinar before extraction could shrink it.

export type UploadItemStatus = "queued" | "extracting" | "uploading" | "done" | "failed" | "cancelled";

export interface UploadItem {
  id: string;
  name: string;
  status: UploadItemStatus;
  error?: string;
  /// 0..1 while `extracting`; undefined in every other status.
  progress?: number;
}

export interface ExtractOptions {
  onProgress: (fraction: number) => void;
  signal: AbortSignal;
}

/// Lets the UI cancel one in-flight item. Handed to the caller once, via `onHandle`.
export interface UploadBatchHandle {
  cancel: (id: string) => void;
}

export interface RunUploadBatchDeps {
  upload: (file: File) => Promise<void>;
  onUpdate: (items: UploadItem[]) => void;
  /// Called after each successful upload (e.g. refresh the recordings list).
  onSuccess?: () => void;
  /// Stage 1. Defaults to `sourceProblem`.
  sourceCheck?: (file: File) => string | null;
  /// Stage 3. Defaults to `resultProblem`.
  precheck?: (file: File) => string | null;
  classify?: (file: File) => MediaKind;
  /// Stage 2. Strips video from a container, returning the ORIGINAL File when it holds none - so an
  /// audio-only WebM is never needlessly re-encoded. Omitted entirely, containers upload as-is.
  extract?: (file: File, opts: ExtractOptions) => Promise<File>;
  /// Receives the cancel handle once, before any work starts.
  onHandle?: (handle: UploadBatchHandle) => void;
}

export async function runUploadBatch(files: File[], deps: RunUploadBatchDeps): Promise<UploadItem[]> {
  const sourceCheck = deps.sourceCheck ?? sourceProblem;
  const precheck = deps.precheck ?? resultProblem;
  const classify = deps.classify ?? classifyFile;
  const items: UploadItem[] = files.map((f, i) => ({ id: `${i}-${f.name}`, name: f.name, status: "queued" }));
  const emit = () => deps.onUpdate(items.map((i) => ({ ...i })));
  const controllers = new Map<string, AbortController>();
  deps.onHandle?.({ cancel: (id) => controllers.get(id)?.abort() });
  emit();

  for (let i = 0; i < files.length; i++) {
    const set = (patch: Partial<UploadItem>) => {
      items[i] = { ...items[i], ...patch };
      emit();
    };

    const problem = sourceCheck(files[i]);
    if (problem) {
      set({ status: "failed", error: problem });
      continue;
    }

    let file = files[i];
    if (classify(files[i]) === "container" && deps.extract) {
      const controller = new AbortController();
      controllers.set(items[i].id, controller);
      set({ status: "extracting", progress: 0 });
      try {
        file = await deps.extract(files[i], {
          onProgress: (p) => set({ progress: p }),
          signal: controller.signal,
        });
      } catch (e) {
        // An abort is the user's choice, not a failure - and either way the video is never uploaded.
        set(
          controller.signal.aborted
            ? { status: "cancelled", progress: undefined }
            : {
                status: "failed",
                progress: undefined,
                error: e instanceof Error ? e.message : "Couldn't extract audio from that video.",
              },
        );
        continue;
      } finally {
        controllers.delete(items[i].id);
      }
    }

    const resultIssue = precheck(file);
    if (resultIssue) {
      set({ status: "failed", progress: undefined, error: resultIssue });
      continue;
    }

    set({ status: "uploading", progress: undefined });
    try {
      await deps.upload(file);
      set({ status: "done" });
      deps.onSuccess?.();
    } catch (e) {
      set({ status: "failed", error: e instanceof Error ? e.message : "Upload failed." });
    }
  }

  return items;
}
