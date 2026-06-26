import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import Recorder from "../components/Recorder";
import KebabMenu, { type KebabAction } from "../components/KebabMenu";
import type { RecordingStatus, RecordingSource, RecordingSummary } from "../lib/types";

const statusColor: Record<RecordingStatus, string> = {
  Uploaded: "bg-gray-100 text-gray-700",
  Queued: "bg-amber-100 text-amber-800",
  Transcribing: "bg-amber-100 text-amber-800",
  Transcribed: "bg-green-100 text-green-800",
  Summarizing: "bg-amber-100 text-amber-800",
  Summarized: "bg-green-100 text-green-800",
  Failed: "bg-red-100 text-red-800",
};

function sourceLabel(s: RecordingSource): string {
  return s === "System" ? "System audio" : "Microphone";
}

export default function Recordings() {
  const qc = useQueryClient();
  const { data: recordings = [], isLoading } = useQuery({
    queryKey: ["recordings"],
    queryFn: api.listRecordings,
  });

  // Live status updates over SignalR refresh the list.
  useEffect(() => {
    const hub = createHub(() => qc.invalidateQueries({ queryKey: ["recordings"] }));
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [qc]);

  return (
    <div className="space-y-6">
      <Recorder onUploaded={() => qc.invalidateQueries({ queryKey: ["recordings"] })} />

      <div>
        <h2 className="mb-2 text-sm font-medium text-gray-500">Recordings</h2>
        {isLoading ? (
          <p className="text-sm text-gray-500">Loading…</p>
        ) : recordings.length === 0 ? (
          <p className="text-sm text-gray-500">No recordings yet. Hit Record above.</p>
        ) : (
          <ul className="divide-y rounded-lg border bg-white">
            {recordings.map((r) => (
              <RecordingRow key={r.id} r={r} />
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function RecordingRow({ r }: { r: RecordingSummary }) {
  const qc = useQueryClient();
  const [renaming, setRenaming] = useState(false);
  const [audioUrl, setAudioUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => qc.invalidateQueries({ queryKey: ["recordings"] });
  const run = (fn: () => Promise<unknown>) => async () => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  };

  async function saveName(name: string) {
    await api.renameRecording(r.id, name.trim() || null);
    setRenaming(false);
    refresh();
  }

  const actions: KebabAction[] = [
    { label: "Rename", onClick: () => setRenaming(true) },
    { label: "Summarise", onClick: run(async () => { await api.summarize(r.id); refresh(); }) },
    { label: "Play", onClick: run(async () => setAudioUrl(await api.audioUrl(r.id))) },
    { label: "Download transcript (.txt)", onClick: run(() => api.downloadTranscript(r.id, "txt")) },
    { label: "Download transcript (.srt)", onClick: run(() => api.downloadTranscript(r.id, "srt")) },
    { label: "Download audio", onClick: run(() => api.downloadAudio(r.id)) },
    {
      label: "Download both",
      onClick: run(async () => {
        await api.downloadTranscript(r.id, "txt");
        await api.downloadAudio(r.id);
      }),
    },
    {
      label: "Delete",
      danger: true,
      onClick: run(async () => {
        if (!window.confirm(`Delete "${r.name ?? r.title}"? This cannot be undone.`)) return;
        await api.deleteRecording(r.id);
        refresh();
      }),
    },
  ];

  return (
    <li className="px-4 py-3">
      <div className="flex items-center justify-between gap-2">
        {renaming ? (
          <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={() => setRenaming(false)} />
        ) : (
          <Link to={`/recordings/${r.id}`} className="min-w-0 flex-1 hover:underline">
            <div className="truncate font-medium">{r.name ?? r.title}</div>
            <div className="text-xs text-gray-500">
              {sourceLabel(r.source)} · {new Date(r.createdAt).toLocaleString()} ·{" "}
              {Math.round(r.durationMs / 1000)}s
            </div>
          </Link>
        )}
        <span className={`shrink-0 rounded px-2 py-0.5 text-xs ${statusColor[r.status]}`}>
          {r.status}
        </span>
        <KebabMenu actions={actions} />
      </div>
      {audioUrl && (
        <audio src={audioUrl} controls autoPlay className="mt-2 w-full" />
      )}
      {error && <p className="mt-1 text-xs text-red-600">{error}</p>}
    </li>
  );
}

function RenameForm({
  initial,
  onSave,
  onCancel,
}: {
  initial: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <form
      className="flex min-w-0 flex-1 items-center gap-2"
      onSubmit={(e) => {
        e.preventDefault();
        onSave(value);
      }}
    >
      <input
        autoFocus
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onKeyDown={(e) => e.key === "Escape" && onCancel()}
        placeholder="Recording name"
        aria-label="Recording name"
        className="min-w-0 flex-1 rounded border px-2 py-1 text-sm"
      />
      <button type="submit" className="rounded border px-2 py-1 text-sm hover:bg-gray-50">
        Save
      </button>
    </form>
  );
}
