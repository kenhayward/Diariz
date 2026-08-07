import { useState } from "react";
import { NavLink, useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../../lib/api";
import { hasTranscript, isProcessing, showStatusBadge, statusBadgeClass, statusLabel } from "../../lib/recordingStatus";
import { sourceLabel } from "../../lib/recordingSource";
import { copyRichLink, transcriptUrl } from "../../lib/clipboard";
import { useRoomBasePath, useSharedRoomId } from "../../lib/rooms";
import { useActiveRecordingId } from "../../lib/activeRoute";
import { useDrillSearch } from "../../lib/drillRoute";
import { formatDuration } from "../../lib/format";
import KebabMenu from "../KebabMenu";
import MoveToSectionModal from "../MoveToSectionModal";
import DownloadTranscriptModal from "../DownloadTranscriptModal";
import { recordingMenu } from "../recordingMenu";
import type { RecordingSummary } from "../../lib/types";

/// One recording in a list: the row you drag, tick, rename, and open. Used by the panel's three lists (the
/// drill-in level, the calendar day list, the tags list) and by the expanded tag-cloud modal.
///
/// Note `useTranslation()` takes **no namespace** here: every key below is explicitly prefixed
/// (`workspace:`, `recordings:`, `common:`) because this row draws on all three, and `recordingMenu`
/// expects the same un-namespaced `t`. Adding a default namespace would silently break every label.

/// A small microphone glyph marking whether a recording still has its audio: green when present,
/// grey once the audio has been deleted. Sits at the start of the row, after the drag handle.
function MicIcon({ on, title }: { on: boolean; title: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      width={14}
      height={14}
      role="img"
      aria-label={title}
      className={`shrink-0 ${on ? "text-green-600 dark:text-green-400" : "text-gray-300 dark:text-gray-600"}`}
    >
      <title>{title}</title>
      <path d="M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z" />
      <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
      <line x1="12" y1="19" x2="12" y2="23" />
      <line x1="8" y1="23" x2="16" y2="23" />
    </svg>
  );
}

/// A small calendar glyph marking that a recording is linked to a Google Calendar event. Tinted the linked
/// calendar's Google colour when known (else green); absent when the recording isn't linked to a meeting.
function CalendarIcon({ title, color }: { title: string; color?: string | null }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      width={14}
      height={14}
      role="img"
      aria-label={title}
      style={color ? { color } : undefined}
      className={`shrink-0 ${color ? "" : "text-green-600 dark:text-green-400"}`}
    >
      <title>{title}</title>
      <rect x="3" y="4" width="18" height="18" rx="2" ry="2" />
      <line x1="16" y1="2" x2="16" y2="6" />
      <line x1="8" y1="2" x2="8" y2="6" />
      <line x1="3" y1="10" x2="21" y2="10" />
    </svg>
  );
}

export function RecordingRow({
  r,
  indentClass,
  selectMode,
  selected,
  onToggleSelect,
  onDropBefore,
  showDate = false,
  onNavigate,
  cut = false,
}: {
  r: RecordingSummary;
  /// Left-padding class that indents the row under its section heading (e.g. "pl-6" / "pl-10").
  indentClass: string;
  selectMode: boolean;
  selected: boolean;
  onToggleSelect: () => void;
  onDropBefore: (draggedId: string) => void;
  /// Show a second line with the source + date/time (the dense list keeps these in the hover title; the
  /// Tags views have room to show them). Off by default so the list/calendar rows are unchanged.
  showDate?: boolean;
  /// Called when the row's name link is clicked - lets the expanded modal close itself as it navigates.
  onNavigate?: () => void;
  /// This recording is the move clipboard's current cut - greyed out with a dashed outline rather than
  /// hidden, since nothing has actually moved yet (see RecordingsPanel's paste flow).
  cut?: boolean;
}) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const basePath = useRoomBasePath();
  const sharedRoomId = useSharedRoomId();
  const activeRecordingId = useActiveRecordingId();
  // The row links back into the drill level it was rendered from. Empty outside the List tab (the
  // Calendar/Tags lists aren't drilled), so those links are unchanged.
  const drillSearch = useDrillSearch();
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
    onCopyLink: run(() => copyRichLink(transcriptUrl(r.id), r.name ?? r.title)),
    onRetranscribe: run(async () => { await api.retranscribe(r.id); refresh(); }),
    onSummarise: run(async () => { await api.summarize(r.id); refresh(); }),
    onExtractActions: run(async () => {
      if (r.hasActions && !window.confirm(t("workspace:confirmReextract"))) return;
      await api.extractActions(r.id);
      refresh();
    }),
    onReidentify: run(async () => { await api.reidentify(r.id); refresh(); }),
    onMove: () => setMoving(true),
    onDownloadTranscript: () => setDownloading(true),
    onEmailTranscript: run(() => api.emailTranscript(r.id)),
    onDownloadAudio: run(() => api.downloadAudio(r.id)),
    onDeleteAudio: run(async () => {
      if (!window.confirm(t("workspace:confirmDeleteAudio", { name: r.name ?? r.title }))) return;
      await api.deleteAudio(r.id);
      refresh();
      qc.invalidateQueries({ queryKey: ["user-storage"] }); // freed quota → refresh the account menu
    }),
    onDelete: run(async () => {
      if (!window.confirm(t("workspace:confirmDelete", { name: r.name ?? r.title }))) return;
      await api.deleteRecording(r.id);
      // If the deleted recording is the one open in the detail panel, leave it — otherwise its transcript
      // stays on screen and any further action targets a now-missing recording.
      if (activeRecordingId === r.id) navigate(basePath || "/");
      refresh();
      qc.invalidateQueries({ queryKey: ["user-storage"] });
    }),
    hasTranscript: hasTranscript(r.status),
    hasAudio: r.hasAudio,
    isSummarizing: r.status === "Summarizing",
    isProcessing: isProcessing(r.status),
  }, t);

  return (
    // The whole row is the drag source (no separate handle) — it already highlights on hover. Dragging is
    // disabled while renaming so text can be selected in the input. The inner NavLink keeps draggable=false
    // so grabbing the name still drags the row, not the link.
    <li
      // `outline` (not `border`) for the cut indicator: this row sits inside a `divide-y` list, whose
      // divider rule targets `border-*` on every child via a compound selector that would outrank (or,
      // for the dark-mode colour, race on stylesheet order against) a plain border class here regardless
      // of class-attribute order. `outline` is a separate CSS property the divider never touches, so the
      // dashed ring is guaranteed visible rather than winning by luck.
      className={`py-0.5 pr-3 ${indentClass} ${cut ? "opacity-50 rounded outline outline-dashed outline-1 outline-gray-400 dark:outline-gray-600" : ""}`}
      draggable={!renaming}
      onDragStart={(e) => {
        e.dataTransfer.setData("text/plain", r.id);
        e.dataTransfer.effectAllowed = "move";
      }}
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        if (e.dataTransfer.files?.length) return; // a file upload — let it bubble to the panel drop zone
        e.preventDefault();
        e.stopPropagation(); // don't also trigger the group's append-drop
        onDropBefore(e.dataTransfer.getData("text/plain"));
      }}
    >
      {/* Colour/opacity alone would leave a screen-reader user unable to tell which rows are cut - the
          bar's count says something is cut, never which. */}
      {cut && <span className="sr-only">{t("workspace:cutPendingPasteAria")}</span>}
      <div className="flex items-center justify-between gap-1">
        {selectMode && (
          <input
            type="checkbox"
            checked={selected}
            onChange={onToggleSelect}
            aria-label={t("workspace:selectRecordingAria", { name: r.name ?? r.title })}
            className="shrink-0"
          />
        )}
        {/* Audio presence: green when the audio is available, grey once it's been deleted. */}
        <MicIcon
          on={r.hasAudio}
          title={r.hasAudio ? t("workspace:hasAudioTitle") : t("workspace:audioDeletedTitle")}
        />
        {/* Calendar link: shown alongside the mic icon when the recording is linked to a meeting, tinted the
            linked calendar's Google colour. */}
        {r.calendarEventId && <CalendarIcon title={t("workspace:hasCalendarTitle")} color={r.calendarColor} />}
        {renaming ? (
          <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={() => setRenaming(false)} />
        ) : (
          <NavLink
            // Keeps `?in=` so opening a recording doesn't pop the list back to the root behind it.
            to={{ pathname: `${basePath}/recordings/${r.id}`, search: drillSearch }}
            draggable={false}
            onClick={() => onNavigate?.()}
            // Single-line row: name + right-aligned duration. Source + date (and the full, untruncated name)
            // move to the hover tooltip to keep the list dense (unless showDate puts the date on a 2nd line).
            title={`${r.name ?? r.title} — ${sourceLabel(r.source, t)} · ${new Date(r.createdAt).toLocaleDateString(i18n.language)}`}
            className={({ isActive }) =>
              `flex min-w-0 flex-1 gap-2 rounded px-1 py-0.5 leading-tight ${showDate ? "items-start" : "items-baseline"} ${
                isActive ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
              }`
            }
          >
            <span className="min-w-0 flex-1">
              <span className="block truncate text-sm font-medium dark:text-gray-100">{r.name ?? r.title}</span>
              {showDate && (
                <span className="block truncate text-xs text-gray-500 dark:text-gray-400">
                  {sourceLabel(r.source, t)} · {new Date(r.createdAt).toLocaleString(i18n.language, { dateStyle: "medium", timeStyle: "short" })}
                </span>
              )}
            </span>
            {/* Duration right-aligned (tabular-nums) so durations line up down the list. */}
            <span className="shrink-0 tabular-nums text-xs text-gray-500 dark:text-gray-400">
              {formatDuration(r.durationMs)}
            </span>
          </NavLink>
        )}
        {showStatusBadge(r.status) && (
          <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] ${statusBadgeClass(r.status)}`}>
            {statusLabel(r.status)}
          </span>
        )}
        <KebabMenu actions={actions} />
      </div>
      {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
      {moving && (
        <MoveToSectionModal recordingId={r.id} currentSectionId={r.sectionId} roomId={sharedRoomId} onClose={() => setMoving(false)} />
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
  const { t } = useTranslation("workspace");
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
        placeholder={t("recordingNamePlaceholder")}
        aria-label={t("recordingNamePlaceholder")}
        className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
      />
      <button type="submit" className="rounded border px-2 py-1 text-xs hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800 dark:text-gray-200">
        {t("common:save")}
      </button>
    </form>
  );
}
