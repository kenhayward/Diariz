import { useRef, useState } from "react";
import { api, apiErrorMessage } from "../lib/api";
import { getStream, isElectron, describeAudioError, type AudioSourceKind } from "../lib/audioSource";

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

  async function start() {
    setError(null);
    try {
      const stream = await getStream(source);
      const recorder = new MediaRecorder(stream, { mimeType: "audio/webm" });
      chunksRef.current = [];
      recorder.ondataavailable = (e) => e.data.size > 0 && chunksRef.current.push(e.data);
      recorder.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        void upload();
      };
      recorder.start();
      recorderRef.current = recorder;
      startRef.current = Date.now();
      setElapsed(0);
      timerRef.current = window.setInterval(
        () => setElapsed(Date.now() - startRef.current),
        250,
      );
      setRecording(true);
    } catch (e) {
      // Log the raw cause (DOMException name/message) so the actual failure is diagnosable.
      console.error("Audio capture failed:", e);
      setError(describeAudioError(e, source, isElectron));
    }
  }

  function stop() {
    if (timerRef.current) window.clearInterval(timerRef.current);
    setRecording(false);
    recorderRef.current?.stop();
  }

  async function upload() {
    setBusy(true);
    try {
      const blob = new Blob(chunksRef.current, { type: "audio/webm" });
      const durationMs = Date.now() - startRef.current;
      const title = `${source === "system" ? "System" : "Mic"} ${new Date().toLocaleString()}`;
      await api.upload(blob, title, durationMs, source === "system" ? "System" : "Microphone");
      onUploaded();
    } catch (e) {
      setError(apiErrorMessage(e, "Upload failed."));
    } finally {
      setBusy(false);
    }
  }

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
            onClick={start}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? "Uploading…" : "Record"}
          </button>
        )}

        {recording && <span className="font-mono text-sm text-red-600">● {mmss}</span>}
        {error && compact && <span className="text-xs text-red-600">{error}</span>}
      </div>
      {error && !compact && <p className="mt-2 text-sm text-red-600">{error}</p>}
    </div>
  );
}
