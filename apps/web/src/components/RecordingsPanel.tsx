import { useEffect, useState } from "react";
import { NavLink } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu, { type KebabAction } from "./KebabMenu";
import type { RecordingStatus, RecordingSource, RecordingSummary } from "../lib/types";

const statusColor: Record<RecordingStatus, string> = {
  Uploaded: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
  Queued: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribed: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Summarizing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Summarized: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Failed: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
};

function sourceLabel(s: RecordingSource): string {
  return s === "System" ? "System audio" : "Microphone";
}

/// The recordings list for the left panel. Selecting a row routes to /recordings/:id (middle panel).
export default function RecordingsPanel() {
  const qc = useQueryClient();
  const { data: recordings = [], isLoading } = useQuery({
    queryKey: ["recordings"],
    queryFn: api.listRecordings,
  });

  useEffect(() => {
    const hub = createHub(() => qc.invalidateQueries({ queryKey: ["recordings"] }));
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [qc]);

  if (isLoading) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">Loading…</p>;
  if (recordings.length === 0)
    return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">No recordings yet. Hit Record above.</p>;

  return (
    <ul className="divide-y dark:divide-gray-800">
      {recordings.map((r) => (
        <RecordingRow key={r.id} r={r} />
      ))}
    </ul>
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
    <li className="px-3 py-2">
      <div className="flex items-center justify-between gap-1">
        {renaming ? (
          <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={() => setRenaming(false)} />
        ) : (
          <NavLink
            to={`/recordings/${r.id}`}
            className={({ isActive }) =>
              `min-w-0 flex-1 rounded px-1 py-0.5 ${
                isActive ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
              }`
            }
          >
            <div className="truncate text-sm font-medium dark:text-gray-100">{r.name ?? r.title}</div>
            <div className="truncate text-xs text-gray-500 dark:text-gray-400">
              {sourceLabel(r.source)} · {new Date(r.createdAt).toLocaleDateString()} ·{" "}
              {Math.round(r.durationMs / 1000)}s
            </div>
          </NavLink>
        )}
        <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] ${statusColor[r.status]}`}>
          {r.status}
        </span>
        <KebabMenu actions={actions} />
      </div>
      {audioUrl && <audio src={audioUrl} controls autoPlay className="mt-2 w-full" />}
      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
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
      className="flex min-w-0 flex-1 items-center gap-1"
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
        className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-1 text-xs hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800 dark:text-gray-200">
        Save
      </button>
    </form>
  );
}
