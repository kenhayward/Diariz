import { uploadableWithoutExtraction } from "./mediaKinds";
import type { ExtractOptions } from "./uploadQueue";
import type { WorkerResponse } from "./videoAudio.worker";

/// Extract mono Opus audio from a container, off the main thread.
///
/// Returns the ORIGINAL File when the container holds no video - an audio-only WebM (say, one of our
/// own recordings being re-uploaded) must not be re-encoded. Throws a plain Error whose message is
/// shown to the user; the caller never falls back to uploading the video.
///
/// The Worker is constructed per call, not at module scope: jsdom has no `Worker`, and this module is
/// imported by uploadContext.tsx, which is component-tested.
export function extractAudio(file: File, opts: ExtractOptions): Promise<File> {
  return new Promise<File>((resolve, reject) => {
    if (typeof Worker === "undefined" || typeof AudioDecoder === "undefined") {
      // No WebCodecs. Fall back to what this app accepted before extraction existed rather than
      // regressing a case that used to work; anything newly accepted is refused, never sent blind.
      if (uploadableWithoutExtraction(file)) resolve(file);
      else
        reject(
          new Error("This browser can't extract audio from video. Try Chrome or Edge, or the desktop app."),
        );
      return;
    }

    const worker = new Worker(new URL("./videoAudio.worker.ts", import.meta.url), { type: "module" });
    // A cancelled or failed conversion is terminated, never reused: a worker wedged mid-conversion
    // would hold its thread and buffers for the rest of the session.
    const finish = (fn: () => void) => {
      opts.signal.removeEventListener("abort", onAbort);
      worker.terminate();
      fn();
    };
    function onAbort() {
      worker.postMessage({ type: "cancel" });
      finish(() => reject(new Error("Cancelled.")));
    }
    opts.signal.addEventListener("abort", onAbort);

    worker.onmessage = (e: MessageEvent<WorkerResponse>) => {
      const m = e.data;
      if (m.type === "progress") opts.onProgress(m.fraction);
      else if (m.type === "passthrough") finish(() => resolve(file));
      else if (m.type === "error") finish(() => reject(new Error(m.message)));
      else if (m.type === "done") {
        const name = file.name.replace(/\.[^.]+$/, "") + ".webm";
        finish(() => resolve(new File([m.buffer], name, { type: "audio/webm" })));
      }
    };
    worker.onerror = () => finish(() => reject(new Error("Couldn't extract audio from that video.")));

    worker.postMessage({ type: "start", file });
  });
}
