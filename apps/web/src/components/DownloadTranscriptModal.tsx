import { useState } from "react";
import { api, apiErrorMessage } from "../lib/api";

const FORMATS = [
  { value: "txt", label: "Plain Text", ext: ".txt" },
  { value: "md", label: "Markdown", ext: ".md" },
  { value: "rtf", label: "Rich Text Format", ext: ".rtf" },
] as const;
type Format = (typeof FORMATS)[number]["value"];

/// "Download as …" chooser: pick a transcript format, then OK to download (or Cancel). Each format is
/// structured like the emailed transcript — name, summary, then the transcript (paragraphs for text, a
/// table for Markdown/RTF).
export default function DownloadTranscriptModal({
  recordingId,
  onClose,
}: {
  recordingId: string;
  onClose: () => void;
}) {
  const [format, setFormat] = useState<Format>("txt");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function ok() {
    setBusy(true);
    setError(null);
    try {
      await api.downloadTranscript(recordingId, format);
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e, "Download failed."));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label="Download transcript"
        className="w-80 rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-base font-semibold dark:text-gray-100">Download as …</h2>
        <div className="mt-3 space-y-1">
          {FORMATS.map((f) => (
            <label
              key={f.value}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              <input
                type="radio"
                name="download-format"
                value={f.value}
                checked={format === f.value}
                onChange={() => setFormat(f.value)}
              />
              <span>{f.label}</span>
              <span className="ml-auto text-xs text-gray-400 dark:text-gray-500">{f.ext}</span>
            </label>
          ))}
        </div>

        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={ok}
            disabled={busy}
            className="rounded bg-blue-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {busy ? "Downloading…" : "OK"}
          </button>
        </div>
      </div>
    </div>
  );
}
