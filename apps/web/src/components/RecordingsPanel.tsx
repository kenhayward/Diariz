import { useEffect, useMemo, useRef, useState } from "react";
import { NavLink } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { createHub } from "../lib/signalr";
import KebabMenu from "./KebabMenu";
import ToolbarButton, { iconProps } from "./ToolbarButton";
import MoveToSectionModal from "./MoveToSectionModal";
import DownloadTranscriptModal from "./DownloadTranscriptModal";
import { recordingMenu } from "./recordingMenu";
import { useSelection } from "../lib/selection";
import { computeReorder } from "../lib/reorder";
import { useUpload } from "../lib/uploadContext";
import type { UploadItem } from "../lib/uploadQueue";
import type { RecordingStatus, RecordingSource, RecordingSummary, SectionDto } from "../lib/types";

const dragHasFiles = (e: React.DragEvent) => Array.from(e.dataTransfer.types ?? []).includes("Files");

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
  if (s === "System") return "System audio";
  if (s === "Upload") return "Uploaded";
  return "Microphone";
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

  // Drag audio files anywhere onto the panel to upload them (distinct from the reorder DnD, which uses
  // the "text/plain" payload — file drags carry "Files"). A depth counter keeps the highlight stable as
  // the cursor moves over child rows.
  const upload = useUpload();
  const [dragging, setDragging] = useState(false);
  const dragDepth = useRef(0);
  function onFileDragEnter(e: React.DragEvent) {
    if (!dragHasFiles(e)) return;
    dragDepth.current += 1;
    setDragging(true);
  }
  function onFileDragLeave(e: React.DragEvent) {
    if (!dragHasFiles(e)) return;
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDragging(false);
  }
  function onFileDragOver(e: React.DragEvent) {
    if (dragHasFiles(e)) e.preventDefault(); // allow drop
  }
  function onFileDrop(e: React.DragEvent) {
    if (!dragHasFiles(e)) return; // a reorder drop — leave it to the row/group handlers
    e.preventDefault();
    setDragging(false);
    dragDepth.current = 0;
    upload.uploadFiles(Array.from(e.dataTransfer.files));
  }

  if (isLoading) return <p className="p-4 text-sm text-gray-500 dark:text-gray-400">Loading…</p>;

  // Show section headings whenever any section exists (so an empty, just-created section is visible).
  const grouped = groups.some((g) => g.id !== null);

  return (
    <div
      onDragEnter={onFileDragEnter}
      onDragLeave={onFileDragLeave}
      onDragOver={onFileDragOver}
      onDrop={onFileDrop}
      className={dragging ? "rounded-md ring-2 ring-inset ring-blue-400 dark:ring-blue-500" : ""}
    >
      <ListToolbar />
      <UploadStatusList items={upload.items} onClear={upload.clearFinished} />
      {dragging && (
        <p className="px-3 py-2 text-center text-xs font-medium text-blue-600 dark:text-blue-400">
          Drop audio files to upload
        </p>
      )}
      {recordings.length === 0 && !dragging && (
        <p className="p-4 text-sm text-gray-500 dark:text-gray-400">
          No recordings yet. Hit Record above, or drop audio files here.
        </p>
      )}
      {groups.map((g) => {
        const ids = g.items.map((i) => i.id);
        const key = g.id ?? UNGROUPED_KEY;
        const isCollapsed = collapsed.has(key);
        // Select mode only: a checkbox that selects/deselects every recording in the group at once.
        const selectAll = selection.selectMode && ids.length > 0 ? (
          <GroupSelectCheckbox
            groupName={g.name}
            ids={ids}
            selectedIds={selection.selectedIds}
            onChange={(checkAll) => {
              const next = new Set(selection.selectedIds);
              if (checkAll) ids.forEach((id) => next.add(id));
              else ids.forEach((id) => next.delete(id));
              selection.set([...next]);
            }}
          />
        ) : undefined;
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
                  leading={selectAll}
                />
              ) : (
                <GroupHeadingButton
                  name={g.name}
                  count={g.items.length}
                  collapsed={isCollapsed}
                  onToggle={() => toggleGroup(key)}
                  withBg
                  leading={selectAll}
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
        <div className="flex items-center gap-0.5">
          <ToolbarButton label="New section" onClick={() => setOpen(true)} icon={<FolderPlusIcon />} />
          <ToolbarButton
            label={selectMode ? "Done selecting" : "Select recordings"}
            onClick={() => setSelectMode(!selectMode)}
            active={selectMode}
            icon={<SelectIcon />}
          />
          {selectMode && selectedIds.length > 0 && (
            <span className="text-xs text-blue-700 dark:text-blue-300">{selectedIds.length}</span>
          )}
        </div>
      )}
    </div>
  );
}

const FolderPlusIcon = () => (
  <svg {...iconProps}>
    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" />
    <line x1="12" y1="11" x2="12" y2="17" />
    <line x1="9" y1="14" x2="15" y2="14" />
  </svg>
);
const SelectIcon = () => (
  <svg {...iconProps}>
    <path d="M9 11l3 3L22 4" />
    <path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" />
  </svg>
);

/// Per-file status for the current upload batch (queued/uploading/done/failed). Tolerant of partial
/// failures — a rejected file shows its reason and the rest still upload.
function UploadStatusList({ items, onClear }: { items: UploadItem[]; onClear: () => void }) {
  if (items.length === 0) return null;
  const settled = items.every((i) => i.status === "done" || i.status === "failed");
  const tag: Record<UploadItem["status"], string> = {
    queued: "text-gray-400",
    uploading: "text-amber-600 dark:text-amber-400",
    done: "text-green-600 dark:text-green-400",
    failed: "text-red-600 dark:text-red-400",
  };
  const label: Record<UploadItem["status"], string> = {
    queued: "Queued",
    uploading: "Uploading…",
    done: "✓ Done",
    failed: "✕ Failed",
  };
  return (
    <div className="border-b px-3 py-2 dark:border-gray-800">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400">Uploads</span>
        {settled && (
          <button type="button" onClick={onClear} className="text-xs text-gray-400 hover:underline">
            Clear
          </button>
        )}
      </div>
      <ul className="space-y-0.5">
        {items.map((i) => (
          <li key={i.id} className="flex items-center justify-between gap-2 text-xs">
            <span className="truncate dark:text-gray-300" title={i.name}>{i.name}</span>
            <span className={`shrink-0 ${tag[i.status]}`} title={i.error}>{label[i.status]}</span>
          </li>
        ))}
      </ul>
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
  leading,
}: {
  name: string;
  count: number;
  collapsed: boolean;
  onToggle: () => void;
  withBg?: boolean;
  /// Optional control rendered left of the chevron (the group select-all checkbox in Select mode). Kept
  /// outside the toggle button so it isn't a nested interactive element.
  leading?: React.ReactNode;
}) {
  return (
    <div
      className={`flex min-w-0 items-center gap-1 ${
        withBg ? "w-full bg-indigo-50 px-3 py-1 dark:bg-indigo-950/40" : "flex-1"
      }`}
    >
      {leading}
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={!collapsed}
        className="flex min-w-0 flex-1 items-center gap-1 text-left"
      >
        <span aria-hidden className="text-[10px] text-indigo-400">{collapsed ? "▸" : "▾"}</span>
        <h3 className="truncate text-xs font-bold uppercase tracking-wide text-indigo-700 dark:text-indigo-300">
          {name}
        </h3>
        <span className="text-[10px] font-normal text-indigo-400 dark:text-indigo-500">({count})</span>
      </button>
    </div>
  );
}

/// The group-level select-all checkbox shown in Select mode: checked when every recording in the group is
/// selected, indeterminate when only some are. Toggling selects/deselects the whole group at once.
function GroupSelectCheckbox({
  groupName,
  ids,
  selectedIds,
  onChange,
}: {
  groupName: string;
  ids: string[];
  selectedIds: string[];
  onChange: (selectAll: boolean) => void;
}) {
  const ref = useRef<HTMLInputElement>(null);
  const selected = ids.filter((id) => selectedIds.includes(id)).length;
  const all = ids.length > 0 && selected === ids.length;
  const some = selected > 0 && !all;
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = some;
  }, [some]);

  return (
    <input
      ref={ref}
      type="checkbox"
      checked={all}
      aria-label={`Select all in ${groupName}`}
      onChange={() => onChange(!all)}
      className="shrink-0"
    />
  );
}

function SectionHeading({
  id,
  name,
  count,
  collapsed,
  onToggle,
  leading,
}: {
  id: string;
  name: string;
  count: number;
  collapsed: boolean;
  onToggle: () => void;
  leading?: React.ReactNode;
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
        <GroupHeadingButton name={name} count={count} collapsed={collapsed} onToggle={onToggle} leading={leading} />
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
  const [downloading, setDownloading] = useState(false);
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
    onDownloadTranscript: () => setDownloading(true),
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
        if (e.dataTransfer.files?.length) return; // a file upload — let it bubble to the panel drop zone
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
      {downloading && <DownloadTranscriptModal recordingId={r.id} onClose={() => setDownloading(false)} />}
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
