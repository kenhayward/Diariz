/// Runs the mediabunny conversion off the main thread. Owns no policy: it peeks for a video track,
/// converts when there is one, and reports back. All decisions about what to do with the result live
/// in videoAudio.ts and the upload queue.
import {
  ALL_FORMATS,
  BlobSource,
  BufferTarget,
  Conversion,
  Input,
  Output,
  Quality,
  WebMOutputFormat,
} from "mediabunny";

export type WorkerRequest = { type: "start"; file: File } | { type: "cancel" };

export type WorkerResponse =
  | { type: "progress"; fraction: number }
  /// The container holds no video: the caller should upload the original file untouched.
  | { type: "passthrough" }
  | { type: "done"; buffer: ArrayBuffer }
  | { type: "error"; message: string };

const post = (m: WorkerResponse, transfer?: Transferable[]) =>
  (self as unknown as Worker).postMessage(m, transfer ?? []);

let conversion: Conversion | null = null;

self.onmessage = async (e: MessageEvent<WorkerRequest>) => {
  if (e.data.type === "cancel") {
    await conversion?.cancel();
    return;
  }

  try {
    const input = new Input({ formats: ALL_FORMATS, source: new BlobSource(e.data.file) });

    if (!(await input.getPrimaryAudioTrack())) {
      post({ type: "error", message: "That video has no audio track." });
      return;
    }

    // Cover art shows up as a single-frame video track; a real video runs for the length of the file.
    // Without this, an audio file with embedded artwork would be needlessly re-encoded.
    const videoTrack = await input.getPrimaryVideoTrack();
    const videoSeconds = videoTrack ? await videoTrack.computeDuration() : 0;
    if (videoSeconds <= 1) {
      post({ type: "passthrough" });
      return;
    }

    const output = new Output({ format: new WebMOutputFormat(), target: new BufferTarget() });
    conversion = await Conversion.init({
      input,
      output,
      video: { discard: true },
      audio: {
        codec: "opus",
        numberOfChannels: 1,
        sampleRate: 48000,
        quality: new Quality({ bitrate: 32e3 }),
      },
    });
    if (!conversion.isValid) {
      post({ type: "error", message: "Couldn't decode this video's audio." });
      return;
    }
    conversion.onProgress = (fraction: number) => post({ type: "progress", fraction });

    await conversion.execute();
    const buffer = output.target.buffer;
    if (!buffer) {
      post({ type: "error", message: "Couldn't extract audio from that video." });
      return;
    }
    post({ type: "done", buffer }, [buffer]);
  } catch (err) {
    post({
      type: "error",
      message: err instanceof Error ? err.message : "Couldn't extract audio from that video.",
    });
  } finally {
    conversion = null;
  }
};
