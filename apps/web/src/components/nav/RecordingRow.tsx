import { NavLink } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { showStatusBadge, statusBadgeClass, statusLabel } from "../../lib/recordingStatus";
import { sourceLabel } from "../../lib/recordingSource";
import { useRoomBasePath } from "../../lib/rooms";
import { useDrillSearch } from "../../lib/drillRoute";
import { formatDuration, formatListDateTime } from "../../lib/format";
import { CalendarIcon, MicIcon } from "../icons";
import KebabMenu from "../KebabMenu";
import RenameForm from "./RenameForm";
import { useRecordingActions } from "./useRecordingActions";
import type { RecordingSummary } from "../../lib/types";

/// One recording in a list: the row you drag, tick, rename, and open. Used by the panel's three lists (the
/// drill-in level, the calendar day list, the tags list) and by the expanded tag-cloud modal.
///
/// Note `useTranslation()` takes **no namespace** here: every key below is explicitly prefixed
/// (`workspace:`, `recordings:`, `common:`) because this row draws on all three, and `recordingMenu`
/// expects the same un-namespaced `t`. Adding a default namespace would silently break every label.

/// Whether a recording still has its audio: green when present, grey once the audio has been deleted.
/// The glyph itself is shared (`icons.tsx`); what belongs to the row is this colour rule.
function AudioMark({ on, title }: { on: boolean; title: string }) {
  return (
    <MicIcon
      size={14}
      title={title}
      className={`shrink-0 ${on ? "text-green-600 dark:text-green-400" : "text-gray-300 dark:text-gray-600"}`}
    />
  );
}

/// Marks a recording linked to a calendar meeting, tinted the linked calendar's own colour when known
/// (else green). Absent when the recording isn't linked.
function CalendarMark({ title, color }: { title: string; color?: string | null }) {
  return (
    <CalendarIcon
      size={14}
      title={title}
      color={color}
      className={`shrink-0 ${color ? "" : "text-green-600 dark:text-green-400"}`}
    />
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
  /// Insert the dragged recording before this one. **Optional**: the List tab omits it while a non-manual
  /// sort is active, because a reorder would write a `Position` the sorted view cannot show - the row would
  /// spring back and read as a broken drag. With no handler the row attaches no drop listener at all, so the
  /// drop bubbles to the level behind it (which appends) instead of being swallowed.
  onDropBefore?: (draggedId: string) => void;
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
  const basePath = useRoomBasePath();
  // The row links back into the drill level it was rendered from. Empty outside the List tab (the
  // Calendar/Tags lists aren't drilled), so those links are unchanged.
  const drillSearch = useDrillSearch();
  // Shared with the Calendar tab's day-grid blocks, so both surfaces drive the same menu and modals.
  const { actions, modals, error, renaming, saveName, cancelRename } = useRecordingActions(r);

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
      // Listeners at all only when the row can reorder. Attaching them unconditionally and no-oping inside
      // would swallow the drop: the level behind the row would never see it, so a drag while sorted would
      // do nothing at all rather than append.
      {...(onDropBefore
        ? {
            onDragOver: (e: React.DragEvent) => e.preventDefault(),
            onDrop: (e: React.DragEvent) => {
              if (e.dataTransfer.files?.length) return; // a file upload — let it bubble to the panel drop zone
              e.preventDefault();
              e.stopPropagation(); // don't also trigger the group's append-drop
              onDropBefore(e.dataTransfer.getData("text/plain"));
            },
          }
        : {})}
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
        <AudioMark
          on={r.hasAudio}
          title={r.hasAudio ? t("workspace:hasAudioTitle") : t("workspace:audioDeletedTitle")}
        />
        {/* Calendar link: shown alongside the mic icon when the recording is linked to a meeting, tinted the
            linked calendar's Google colour. */}
        {r.calendarEventId && <CalendarMark title={t("workspace:hasCalendarTitle")} color={r.calendarColor} />}
        {renaming ? (
          <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={cancelRename} />
        ) : (
          <NavLink
            // Keeps `?in=` so opening a recording doesn't pop the list back to the root behind it.
            to={{ pathname: `${basePath}/recordings/${r.id}`, search: drillSearch }}
            draggable={false}
            onClick={() => onNavigate?.()}
            // Single-line row: name + right-aligned date/time. Source, the full date and the duration (which
            // the row no longer shows) move to the hover tooltip to keep the list dense.
            title={`${r.name ?? r.title} - ${sourceLabel(r.source, t)} · ${new Date(r.createdAt).toLocaleDateString(i18n.language)} · ${formatDuration(r.durationMs)}`}
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
            {/* When, right-aligned (tabular-nums) so the column lines up down the list. The duration moved
                into the hover title above: "when was this?" is what a list is scanned for. */}
            <span className="shrink-0 tabular-nums text-xs text-gray-500 dark:text-gray-400">
              {formatListDateTime(r.createdAt, i18n.language, t("workspace:today"))}
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
      {modals}
    </li>
  );
}
