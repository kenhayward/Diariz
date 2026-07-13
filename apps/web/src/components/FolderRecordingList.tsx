import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { NavLink } from "react-router-dom";
import { api } from "../lib/api";
import { formatDuration, formatDate } from "../lib/format";
import { buildRecordingTree, type SectionNode } from "../lib/recordingTree";
import { useRoom, useRoomBasePath } from "../lib/rooms";
import type { RecordingSummary } from "../lib/types";

/// Read-only list of the transcripts in a folder, grouped by sub-folder (click a row to open it). Mirrors the
/// sidebar's format without its interactivity (no drag/drop, kebab, or checkboxes). Reads the already-cached
/// recordings + sections queries and renders this section's node from the tree.
export default function FolderRecordingList({ sectionId }: { sectionId: string }) {
  const { t, i18n } = useTranslation("workspace");
  // Scope to the room the folder page is viewing (its own room), matching the sidebar's RecordingsPanel so
  // the cache is shared. Without this the queries returned the caller's personal library, so a folder in a
  // shared room found no matching section and rendered an empty list (issue #295). `currentRoom?.id` is
  // undefined only briefly while the rooms list loads.
  const { currentRoom } = useRoom();
  const roomId = currentRoom?.id;
  // Keep row links inside the current room - a shared room's detail routes carry the /rooms/:id prefix, so
  // without it a click falls back to the personal-room URL and reopens the recording there (leaving the shared
  // room). Mirrors the sidebar's RecordingsPanel.
  const basePath = useRoomBasePath();
  const { data: recordings } = useQuery({ queryKey: ["recordings", roomId], queryFn: () => api.listRecordings(roomId) });
  const { data: sections } = useQuery({ queryKey: ["sections", roomId], queryFn: () => api.listSections(roomId) });

  const node = useMemo(() => {
    if (!recordings || !sections) return null;
    const tree = buildRecordingTree(recordings, sections);
    return findNode(tree.sections, sectionId);
  }, [recordings, sections, sectionId]);

  if (!node) return null;

  const total = node.items.length + node.children.reduce((n, c) => n + c.items.length, 0);
  if (total === 0)
    return <p className="px-4 pb-4 text-sm text-gray-500 dark:text-gray-400">{t("folderNoRecordings")}</p>;

  return (
    <div className="px-4 pb-4">
      <ul className="divide-y dark:divide-gray-800">
        {node.items.map((r) => <RecordingRow key={r.id} r={r} lang={i18n.language} basePath={basePath} />)}
      </ul>
      {node.children.map((child) =>
        child.items.length === 0 ? null : (
          <div key={child.id} className="mt-3">
            <div className="mb-1 text-xs font-bold uppercase tracking-wide text-indigo-700 dark:text-indigo-300">
              {child.name} <span className="font-normal text-indigo-400">({child.items.length})</span>
            </div>
            <ul className="divide-y dark:divide-gray-800">
              {child.items.map((r) => <RecordingRow key={r.id} r={r} lang={i18n.language} basePath={basePath} />)}
            </ul>
          </div>
        ),
      )}
    </div>
  );
}

function RecordingRow({ r, lang, basePath }: { r: RecordingSummary; lang: string; basePath: string }) {
  return (
    <li>
      <NavLink
        to={`${basePath}/recordings/${r.id}`}
        className="flex items-center justify-between gap-2 py-1.5 hover:bg-gray-50 dark:hover:bg-gray-800/60"
      >
        <span className="min-w-0 flex-1 truncate text-sm text-gray-800 dark:text-gray-100">{r.name ?? r.title}</span>
        <span className="shrink-0 text-xs text-gray-400 dark:text-gray-500">
          {formatDate(r.createdAt, lang)}
          {r.durationMs > 0 ? ` · ${formatDuration(r.durationMs)}` : ""}
        </span>
      </NavLink>
    </li>
  );
}

/// Depth-first search for the section node with the given id (two-level tree, so children have no children).
function findNode(nodes: SectionNode[], id: string): SectionNode | null {
  for (const n of nodes) {
    if (n.id === id) return n;
    const inChild = findNode(n.children, id);
    if (inChild) return inChild;
  }
  return null;
}
