import { useEffect, useRef, useState, type ChangeEvent } from "react";
import { api, apiErrorMessage } from "../lib/api";
import { getStream, isElectron, describeAudioError, type AudioSourceKind } from "../lib/audioSource";
import { connectTrayRecorder, type RecorderState, type TrayBridge } from "../lib/trayRecorder";
import { AUDIO_ACCEPT_ATTR } from "../lib/audioFormats";
import { useUpload } from "../lib/uploadContext";

export default function Recorder({
  onUploaded,
  compact = false,
}: {
  onUploaded: () => void;
  compact?: boolean;
}) {
  const [source, setSource] = useState<AudioSourceKind>("mic");
  const [recording, setRecording] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const recorderRef = useRef<MediaRecorder | null>(null);
  const chunksRef = useRef<Blob[]>([]);
  const startRef = useRef(0);
  const timerRef = useRef<number | null>(null);
  // The source actually being recorded (tray commands pass it explicitly, so we
  // can't rely on the `source` state having flushed by upload time).
  const activeSourceRef = useRef<AudioSourceKind>("mic");
  // Reports phase changes to the Electron tray; a no-op in a plain browser.
  const reportRef = useRef<(s: RecorderState) => void>(() => {});

  async function start(kind: AudioSourceKind = source) {
    setError(null);
    try {
      const stream = await getStream(kind);
      const recorder = new MediaRecorder(stream, { mimeType: "audio/webm" });
      chunksRef.current = [];
      recorder.ondataavailable = (e) => e.data.size > 0 && chunksRef.current.push(e.data);
      recorder.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        void upload();
      };
      recorder.start();
      recorderRef.current = recorder;
      activeSourceRef.current = kind;
      setSource(kind);
      startRef.current = Date.now();
      setElapsed(0);
      timerRef.current = window.setInterval(
        () => setElapsed(Date.now() - startRef.current),
        250,
      );
      setRecording(true);
      reportRef.current({ phase: "recording", source: kind });
    } catch (e) {
      // Log the raw cause (DOMException name/message) so the actual failure is diagnosable.
      console.error("Audio capture failed:", e);
      const message = describeAudioError(e, kind, isElectron);
      setError(message);
      reportRef.current({ phase: "error", error: message });
    }
  }

  function stop() {
    if (timerRef.current) window.clearInterval(timerRef.current);
    setRecording(false);
    recorderRef.current?.stop();
  }

  async function upload() {
    setBusy(true);
    reportRef.current({ phase: "uploading" });
    try {
      const blob = new Blob(chunksRef.current, { type: "audio/webm" });
      const durationMs = Date.now() - startRef.current;
      const kind = activeSourceRef.current;
      const title = `${kind === "system" ? "System" : "Mic"} ${new Date().toLocaleString()}`;
      await api.upload(blob, title, durationMs, kind === "system" ? "System" : "Microphone");
      onUploaded();
      reportRef.current({ phase: "idle" });
    } catch (e) {
      const message = apiErrorMessage(e, "Upload failed.");
      setError(message);
      reportRef.current({ phase: "error", error: message });
    } finally {
      setBusy(false);
    }
  }

  // Upload existing audio files (the "Upload" button). The shared upload queue handles validation,
  // per-file status, and refreshing the list; you can also drag files onto the recordings panel.
  const uploads = useUpload();
  const fileRef = useRef<HTMLInputElement>(null);
  function onPickFiles(e: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(e.target.files ?? []);
    e.target.value = ""; // let the user re-pick the same file later
    uploads.uploadFiles(files);
  }

  // Keep the tray bridge pointed at the latest start/stop without reconnecting.
  const startFn = useRef(start);
  startFn.current = start;
  const stopFn = useRef(stop);
  stopFn.current = stop;

  // Connect the Electron tray to this (single) recorder instance. Tray "start"/"stop"
  // drive the same recorder as the on-screen button; we report phase back so the tray
  // shows the live timer and raises notifications. No-op outside the desktop shell.
  useEffect(() => {
    const diariz = (window as unknown as { diariz?: TrayBridge }).diariz;
    const conn = connectTrayRecorder(diariz, {
      onStart: (src) => void startFn.current(src),
      onStop: () => stopFn.current(),
    });
    reportRef.current = conn.reportState;
    return () => {
      conn.dispose();
      reportRef.current = () => {};
    };
  }, []);

  const secs = Math.floor(elapsed / 1000);
  const mmss = `${String(Math.floor(secs / 60)).padStart(2, "0")}:${String(secs % 60).padStart(2, "0")}`;

  return (
    <div className={compact ? "" : "rounded-lg border bg-white p-4 dark:border-gray-700 dark:bg-gray-900"}>
      <div className="flex items-center gap-2">
        <select
          value={source}
          onChange={(e) => setSource(e.target.value as AudioSourceKind)}
          disabled={recording}
          className="rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        >
          <option value="mic">Microphone</option>
          <option value="system">System audio{isElectron ? "" : " (desktop only)"}</option>
        </select>

        {recording ? (
          <button onClick={stop} className="rounded bg-red-600 px-3 py-1.5 text-sm text-white">
            Stop
          </button>
        ) : (
          <button
            onClick={() => start()}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? "Uploading…" : "Record"}
          </button>
        )}

        <button
          type="button"
          onClick={() => fileRef.current?.click()}
          disabled={recording || busy}
          title="Upload audio files to transcribe (or drag them onto the recordings panel)"
          className="rounded border px-3 py-1.5 text-sm disabled:opacity-50 dark:border-gray-700 dark:text-gray-100 dark:hover:bg-gray-800"
        >
          Upload
        </button>
        <input
          ref={fileRef}
          type="file"
          accept={AUDIO_ACCEPT_ATTR}
          multiple
          onChange={onPickFiles}
          className="hidden"
          data-testid="upload-input"
        />

        {recording && <span className="font-mono text-sm text-red-600">● {mmss}</span>}
        {error && compact && <span className="text-xs text-red-600">{error}</span>}
      </div>
      {error && !compact && <p className="mt-2 text-sm text-red-600">{error}</p>}
    </div>
  );
}
