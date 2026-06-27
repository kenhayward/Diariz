import { useEffect, useMemo, useState } from "react";
import { NavLink } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu from "./KebabMenu";
import MoveToSectionModal from "./MoveToSectionModal";
import { recordingMenu } from "./recordingMenu";
import { useSelection } from "../lib/selection";
import { computeReorder } from "../lib/reorder";
import type { RecordingStatus, RecordingSource, RecordingSummary, SectionDto } from "../lib/types";

const statusColor: Record<RecordingStatus, string> = {
  Uploaded: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
  Queued: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Transcribed: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Summarizing: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  Summarized: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Failed: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
};

const COLLAPSE_KEY = "diariz.recordings.collapsedGroups";
const UNGROUPED_KEY = "__ungrouped__";

function sourceLabel(s: RecordingSource): string {
  return s === "System" ? "System audio" : "Microphone";
}

export function hasTranscript(status: RecordingStatus): boolean {
  return status === "Transcribed" || status === "Summarizing" || status === "Summarized";
}

/// Show the status pill only while the pipeline is moving. The settled success states
/// (Transcribed/Summarized) repeat on every row and truncate the name, so they're hidden.
export function showStatusBadge(status: RecordingStatus): boolean {
  return status !== "Transcribed" && status !== "Summarized";
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
  const { data: sections = [] } = useQuery({ queryKey: ["sections"], queryFn: api.listSections });

  useEffect(() => {
    const hub = createHub(() => qc.invalidateQueries({ queryKey: ["recordings"] }));
    hub.start().catch(() => {});
    return () => void hub.stop();
  }, [qc]);

  const groups = useMemo(() => groupBySection(recordings, sections), [recordings, sections]);
  const selection = useSelection();
  const [collapsed, setCollapsed] = useState<Set<string>>(() => {
    try {
      return new Set<string>(JSON.parse(localStorage.getItem(COLLAPSE_KEY) ?? "[]"));
    } catch {
      return new Set<string>();
    }
  });
  function toggleGroup(key: string) {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      localStorage.setItem(COLLAPSE_KEY, JSON.stringify([...next]));
      return next;
    });
  }

  /// Apply a drag-and-drop: set the dragged recording's group + order, then refresh.
  async function drop(sectionId: string | null, groupIds: string[], draggedId: string, beforeId: string | null) {
    if (!draggedId) return;
    await api.reorderRecordings(sectionId, computeReorder(groupIds, draggedId, beforeId));
    qc.invalidateQueries({ queryKey: ["recordings"] });
  }

  if (isLoading) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">Loading…</p>;

  // Show section headings whenever any section exists (so an empty, just-created section is visible).
  const grouped = groups.some((g) => g.id !== null);

  return (
    <div>
      <ListToolbar />
      {recordings.length === 0 && (
        <p className="p-4 text-sm text-gray-500 dark:text-gray-400">No recordings yet. Hit Record above.</p>
      )}
      {groups.map((g) => {
        const ids = g.items.map((i) => i.id);
        const key = g.id ?? UNGROUPED_KEY;
        const isCollapsed = collapsed.has(key);
        return (
          <div
            key={key}
            // Dropping anywhere in the group (incl. the heading) appends to it.
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => {
              const dragged = e.dataTransfer.getData("text/plain");
              if (dragged) drop(g.id, ids, dragged, null);
            }}
          >
            {grouped &&
              (g.id ? (
                <SectionHeading
                  id={g.id}
                  name={g.name}
                  count={g.items.length}
                  collapsed={isCollapsed}
                  onToggle={() => toggleGroup(key)}
                />
              ) : (
                <GroupHeadingButton
                  name={g.name}
                  count={g.items.length}
                  collapsed={isCollapsed}
                  onToggle={() => toggleGroup(key)}
                  withBg
                />
              ))}
            {!isCollapsed && (
              <ul className="divide-y dark:divide-gray-800">
                {g.items.map((r) => (
                  <RecordingRow
                    key={r.id}
                    r={r}
                    selectMode={selection.selectMode}
                    selected={selection.selectedIds.includes(r.id)}
                    onToggleSelect={() => selection.toggle(r.id)}
                    onDropBefore={(draggedId) => drop(g.id, ids, draggedId, r.id)}
                  />
                ))}
              </ul>
            )}
          </div>
        );
      })}
    </div>
  );
}

/// Top-of-list toolbar: create a section (group) and toggle multi-select for picking chat context.
function ListToolbar() {
  const qc = useQueryClient();
  const { selectMode, setSelectMode, selectedIds } = useSelection();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    const n = name.trim();
    if (!n) return;
    setBusy(true);
    try {
      await api.createSection(n);
      qc.invalidateQueries({ queryKey: ["sections"] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
      setName("");
      setOpen(false);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex items-center justify-between gap-2 border-b px-3 py-1.5 dark:border-gray-800">
      {open ? (
        <form onSubmit={create} className="flex min-w-0 flex-1 items-center gap-1">
          <input
            autoFocus
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && setOpen(false)}
            placeholder="New section name"
            aria-label="New section name"
            className="min-w-0 flex-1 rounded border px-2 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <button
            type="submit"
            disabled={busy || !name.trim()}
            className="rounded border px-2 py-0.5 text-xs hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            Create
          </button>
        </form>
      ) : (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="text-xs font-medium text-indigo-600 hover:underline dark:text-indigo-400"
        >
          + New section
        </button>
      )}
      <button
        type="button"
        onClick={() => setSelectMode(!selectMode)}
        aria-pressed={selectMode}
        className={`shrink-0 rounded border px-2 py-0.5 text-xs dark:border-gray-700 ${
          selectMode
            ? "border-blue-500 bg-blue-50 text-blue-700 dark:bg-blue-900/40 dark:text-blue-300"
            : "hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
        }`}
      >
        {selectMode ? `Done${selectedIds.length ? ` (${selectedIds.length})` : ""}` : "Select"}
      </button>
    </div>
  );
}

function groupBySection(recordings: RecordingSummary[], sections: SectionDto[]): Group[] {
  // Seed with every section so an empty (just-created) section still renders a heading.
  const byId = new Map<string, Group>();
  for (const s of sections) byId.set(s.id, { id: s.id, name: s.name, items: [] });

  const ungrouped: RecordingSummary[] = [];
  for (const r of recordings) {
    if (!r.sectionId) {
      ungrouped.push(r);
      continue;
    }
    // Fall back to the recording's own sectionName if the sections list hasn't loaded yet.
    const g = byId.get(r.sectionId) ?? { id: r.sectionId, name: r.sectionName ?? "Section", items: [] };
    g.items.push(r);
    byId.set(r.sectionId, g);
  }

  const ordered = [...byId.values()].sort((a, b) => a.name.localeCompare(b.name));
  if (ungrouped.length) ordered.push({ id: null, name: "Ungrouped", items: ungrouped });
  return ordered;
}


/// A clickable group header (chevron + name + count) that collapses/expands the group.
/// `withBg` makes it a full-width bar (used for the headingless Ungrouped group); inside a
/// SectionHeading the surrounding row already provides the background.
function GroupHeadingButton({
  name,
  count,
  collapsed,
  onToggle,
  withBg = false,
}: {
  name: string;
  count: number;
  collapsed: boolean;
  onToggle: () => void;
  withBg?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={!collapsed}
      className={`flex min-w-0 items-center gap-1 text-left ${
        withBg ? "w-full bg-indigo-50 px-3 py-1 dark:bg-indigo-950/40" : "flex-1"
      }`}
    >
      <span aria-hidden className="text-[10px] text-indigo-400">{collapsed ? "▸" : "▾"}</span>
      <h3 className="truncate text-xs font-bold uppercase tracking-wide text-indigo-700 dark:text-indigo-300">
        {name}
      </h3>
      <span className="text-[10px] font-normal text-indigo-400 dark:text-indigo-500">{count}</span>
    </button>
  );
}

function SectionHeading({
  id,
  name,
  count,
  collapsed,
  onToggle,
}: {
  id: string;
  name: string;
  count: number;
  collapsed: boolean;
  onToggle: () => void;
}) {
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
        <GroupHeadingButton name={name} count={count} collapsed={collapsed} onToggle={onToggle} />
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

function RecordingRow({
  r,
  selectMode,
  selected,
  onToggleSelect,
  onDropBefore,
}: {
  r: RecordingSummary;
  selectMode: boolean;
  selected: boolean;
  onToggleSelect: () => void;
  onDropBefore: (draggedId: string) => void;
}) {
  const qc = useQueryClient();
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
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
    onDownloadTxt: run(() => api.downloadTranscript(r.id, "txt")),
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
    <li
      className="px-3 py-2"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault();
        e.stopPropagation(); // don't also trigger the group's append-drop
        onDropBefore(e.dataTransfer.getData("text/plain"));
      }}
    >
      <div className="flex items-center justify-between gap-1">
        {selectMode && (
          <input
            type="checkbox"
            checked={selected}
            onChange={onToggleSelect}
            aria-label={`Select ${r.name ?? r.title}`}
            className="shrink-0"
          />
        )}
        {/* Drag handle — grab here to reorder or move between groups. */}
        <span
          draggable
          onDragStart={(e) => {
            e.dataTransfer.setData("text/plain", r.id);
            e.dataTransfer.effectAllowed = "move";
          }}
          aria-label="Drag to reorder"
          className="shrink-0 cursor-grab select-none px-0.5 text-gray-300 hover:text-gray-500 dark:text-gray-600 dark:hover:text-gray-400"
        >
          ⠿
        </span>
        {renaming ? (
          <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={() => setRenaming(false)} />
        ) : (
          <NavLink
            to={`/recordings/${r.id}`}
            draggable={false}
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
        {showStatusBadge(r.status) && (
          <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] ${statusColor[r.status]}`}>
            {r.status}
          </span>
        )}
        <KebabMenu actions={actions} />
      </div>
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
