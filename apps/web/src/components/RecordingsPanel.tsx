import { useEffect, useMemo, useState } from "react";
import { NavLink } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu from "./KebabMenu";
import MoveToSectionModal from "./MoveToSectionModal";
import { recordingMenu } from "./recordingMenu";
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

export function hasTranscript(status: RecordingStatus): boolean {
  return status === "Transcribed" || status === "Summarizing" || status === "Summarized";
}

interface Group {
  id: string | null; // section id, or null for ungrouped
  name: string;
  items: RecordingSummary[];
}

/// The recordings list for the left panel, grouped into user sections (Ungrouped last).
/// Selecting a row routes to /recordings/:id (middle panel).
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

  const groups = useMemo(() => groupBySection(recordings), [recordings]);

  if (isLoading) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">Loading…</p>;
  if (recordings.length === 0)
    return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">No recordings yet. Hit Record above.</p>;

  const grouped = groups.length > 1; // headings only matter once a section exists

  return (
    <div>
      {groups.map((g) => (
        <div key={g.id ?? "__ungrouped__"}>
          {grouped &&
            (g.id ? (
              <SectionHeading id={g.id} name={g.name} />
            ) : (
              <h3 className="bg-indigo-50 px-3 py-1 text-xs font-bold uppercase tracking-wide text-indigo-700 dark:bg-indigo-950/40 dark:text-indigo-300">
                {g.name}
              </h3>
            ))}
          <ul className="divide-y dark:divide-gray-800">
            {g.items.map((r) => (
              <RecordingRow key={r.id} r={r} />
            ))}
          </ul>
        </div>
      ))}
    </div>
  );
}

function groupBySection(recordings: RecordingSummary[]): Group[] {
  const sections = new Map<string, Group>();
  const ungrouped: RecordingSummary[] = [];
  for (const r of recordings) {
    if (r.sectionId) {
      const g = sections.get(r.sectionId) ?? { id: r.sectionId, name: r.sectionName ?? "Section", items: [] };
      g.items.push(r);
      sections.set(r.sectionId, g);
    } else {
      ungrouped.push(r);
    }
  }
  const ordered = [...sections.values()].sort((a, b) => a.name.localeCompare(b.name));
  if (ungrouped.length) ordered.push({ id: null, name: "Ungrouped", items: ungrouped });
  return ordered;
}

function SectionHeading({ id, name }: { id: string; name: string }) {
  const qc = useQueryClient();
  const [renaming, setRenaming] = useState(false);
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["recordings"] });
    qc.invalidateQueries({ queryKey: ["sections"] });
  };

  async function save(newName: string) {
    const n = newName.trim();
    if (n && n !== name) {
      await api.renameSection(id, n);
      refresh();
    }
    setRenaming(false);
  }

  const actions = [
    { label: "Rename", onClick: () => setRenaming(true) },
    {
      label: "Delete",
      danger: true,
      onClick: async () => {
        if (!window.confirm(`Delete section "${name}"? Its recordings move to Ungrouped.`)) return;
        await api.deleteSection(id);
        refresh();
      },
    },
  ];

  return (
    <div className="flex items-center justify-between bg-indigo-50 px-3 py-1 dark:bg-indigo-950/40">
      {renaming ? (
        <SectionRenameForm initial={name} onSave={save} onCancel={() => setRenaming(false)} />
      ) : (
        <h3 className="truncate text-xs font-bold uppercase tracking-wide text-indigo-700 dark:text-indigo-300">
          {name}
        </h3>
      )}
      <KebabMenu actions={actions} label="Section actions" />
    </div>
  );
}

function SectionRenameForm({
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
        aria-label="Section name"
        className="min-w-0 flex-1 rounded border px-2 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-0.5 text-xs hover:bg-white/50 dark:border-gray-700 dark:hover:bg-gray-800">
        Save
      </button>
    </form>
  );
}

function RecordingRow({ r }: { r: RecordingSummary }) {
  const qc = useQueryClient();
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
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

  const actions = recordingMenu({
    onRename: () => setRenaming(true),
    onRetranscribe: run(async () => { await api.retranscribe(r.id); refresh(); }),
    onSummarise: run(async () => { await api.summarize(r.id); refresh(); }),
    onMove: () => setMoving(true),
    onPlay: run(async () => setAudioUrl(await api.audioUrl(r.id))),
    onDownloadTxt: run(() => api.downloadTranscript(r.id, "txt")),
    onDownloadSrt: run(() => api.downloadTranscript(r.id, "srt")),
    onDownloadAudio: run(() => api.downloadAudio(r.id)),
    onDelete: run(async () => {
      if (!window.confirm(`Delete "${r.name ?? r.title}"? This cannot be undone.`)) return;
      await api.deleteRecording(r.id);
      refresh();
    }),
    hasTranscript: hasTranscript(r.status),
    isSummarizing: r.status === "Summarizing",
  });

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
      {moving && (
        <MoveToSectionModal recordingId={r.id} currentSectionId={r.sectionId} onClose={() => setMoving(false)} />
      )}
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
