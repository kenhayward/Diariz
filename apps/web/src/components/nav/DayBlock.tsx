import type { CSSProperties, DragEvent, ReactNode } from "react";
import { NavLink, useMatch } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { showStatusBadge, statusBadgeClass, statusLabel } from "../../lib/recordingStatus";
import { recordingSpan } from "../../lib/calendar";
import { formatDuration } from "../../lib/format";
import { useRoomBasePath } from "../../lib/rooms";
import { useDrillSearch } from "../../lib/drillRoute";
import { useActiveRecordingId } from "../../lib/activeRoute";
import { useSelection } from "../../lib/selection";
import KebabMenu from "../KebabMenu";
import RenameForm from "./RenameForm";
import { useRecordingActions } from "./useRecordingActions";
import type { PositionedBlock } from "../../lib/dayGrid";
import type { DayGridItem } from "./DayGrid";
import type { CalendarEvent, RecordingSummary } from "../../lib/types";

/// One item drawn on the Calendar tab's day grid - a recording or a meeting, positioned and sized by the
/// layout. The two variants live in one file deliberately: they must stay in visual lockstep, and all that
/// genuinely differs is their colour, their second line, and whether they carry a menu.

/// The gap between side-by-side columns, as a percentage of the block column. Distributed so the columns
/// still fill the width - two columns are 49% wide at 0% and 51%, not 48% with a hole at the right edge.
const GUTTER_PCT = 2;

const pct = (n: number) => `${Math.round(n * 100) / 100}%`;

/// A trigger sized for a block rather than a list row: a 30-minute meeting is 22px tall, and the row's
/// ~28px kebab would push the text out of its own block.
const COMPACT_KEBAB =
  "-mr-1 shrink-0 self-start rounded px-1 text-[12px] leading-[14px] text-gray-500 hover:bg-black/5 dark:text-gray-400 dark:hover:bg-white/10";

export default function DayBlock({ block, locale }: { block: PositionedBlock<DayGridItem>; locale: string }) {
  const item = block.item.dayItem;
  return item.type === "recording" ? (
    <RecordingBlock block={block} r={item.recording} locale={locale} />
  ) : (
    <MeetingBlock block={block} event={item.event} locale={locale} />
  );
}

/// Geometry, fill and the 3px identity strip - everything both variants share. `overflow-hidden` lives on
/// the *content*, never here: the wrapper holds the kebab, whose popover this would clip away.
function BlockShell({
  block,
  className,
  edgeClassName,
  edgeStyle,
  active,
  title,
  draggable,
  onDragStart,
  children,
}: {
  block: PositionedBlock<DayGridItem>;
  className: string;
  edgeClassName?: string;
  /// Inline only when the colour cannot be a class - a calendar's own hex from the server.
  edgeStyle?: CSSProperties;
  active: boolean;
  title: string;
  draggable?: boolean;
  onDragStart?: (e: DragEvent) => void;
  children: ReactNode;
}) {
  const { columnCount, columnIndex, leftPct, widthPct } = block;
  return (
    <div
      role="listitem"
      data-testid="day-block"
      title={title}
      draggable={draggable}
      onDragStart={onDragStart}
      style={{
        top: block.top,
        height: block.height,
        left: pct(leftPct + (columnIndex * GUTTER_PCT) / columnCount),
        width: pct(widthPct - (GUTTER_PCT * (columnCount - 1)) / columnCount),
      }}
      className={[
        "absolute flex gap-[5px] rounded-lg px-[7px] py-[3px] leading-tight",
        // A clipped edge is squared off, so an item running past the window reads as continuing rather
        // than as ending there.
        block.clippedStart ? "rounded-t-none" : "",
        block.clippedEnd ? "rounded-b-none" : "",
        // A ring rather than a fill change, so the meeting/recording colour still reads when it is open.
        active ? "ring-2 ring-inset ring-blue-400" : "",
        className,
      ].join(" ")}
    >
      <span aria-hidden className={`absolute inset-y-0 left-0 w-[3px] ${edgeClassName ?? ""}`} style={edgeStyle} />
      {children}
    </div>
  );
}

function RecordingBlock({
  block,
  r,
  locale,
}: {
  block: PositionedBlock<DayGridItem>;
  r: RecordingSummary;
  locale: string;
}) {
  const { t } = useTranslation();
  const basePath = useRoomBasePath();
  const drillSearch = useDrillSearch();
  const selection = useSelection();
  const activeRecordingId = useActiveRecordingId();
  const { actions, modals, error, renaming, saveName, cancelRename } = useRecordingActions(r);

  const name = r.name ?? r.title;
  const { start, end } = recordingSpan(r);
  const startLabel = formatTime(start, locale);
  const duration = formatDuration(r.durationMs);
  // A 22px block has no room for the row's error line, so a failed action surfaces in the tooltip.
  const tooltip = `${name}\n${startLabel} - ${formatTime(end, locale)} · ${duration}${error ? `\n${error}` : ""}`;

  const body = block.tall ? (
    <>
      <span className="block truncate text-[12px] font-semibold text-green-950 dark:text-green-100">{name}</span>
      <span className="flex items-center gap-[5px] overflow-hidden">
        <span className="shrink-0 text-[10px] tabular-nums text-green-800 dark:text-green-300">{startLabel}</span>
        <span className="shrink-0 rounded-[3px] bg-green-200 px-1 text-[9px] font-semibold tabular-nums text-green-950 dark:bg-green-500/25 dark:text-green-200">
          {duration}
        </span>
        {showStatusBadge(r.status) && (
          <span className={`shrink-0 rounded-[3px] px-1 text-[9px] ${statusBadgeClass(r.status)}`}>
            {statusLabel(r.status)}
          </span>
        )}
      </span>
    </>
  ) : (
    // Too short for two lines: the time joins the title. The name stays its own node so it is still
    // findable, selectable and translatable on its own.
    <span className="flex items-baseline gap-1 overflow-hidden whitespace-nowrap">
      <span className="truncate text-[12px] font-semibold text-green-950 dark:text-green-100">{name}</span>
      <span className="shrink-0 text-[10px] tabular-nums text-green-800 dark:text-green-300">
        · {startLabel} · {duration}
      </span>
    </span>
  );

  return (
    <BlockShell
      block={block}
      title={tooltip}
      active={activeRecordingId === r.id}
      edgeClassName="bg-[linear-gradient(180deg,#22c55e,#15803d)] dark:bg-[linear-gradient(180deg,#22c55e,#166534)]"
      className="bg-green-100 hover:bg-green-200 dark:bg-green-500/[0.16] dark:hover:bg-green-500/25"
      // Still a drag *source* (a recording can be dragged out of the calendar onto a folder); the Calendar
      // tab passes a no-op onDropBefore, so blocks are not drop targets.
      draggable={!renaming}
      onDragStart={(e) => {
        e.dataTransfer.setData("text/plain", r.id);
        e.dataTransfer.effectAllowed = "move";
      }}
    >
      {selection.selectMode && (
        <input
          type="checkbox"
          checked={selection.selectedIds.includes(r.id)}
          onChange={() => selection.toggle(r.id)}
          aria-label={t("workspace:selectRecordingAria", { name })}
          className="mt-0.5 shrink-0 self-start"
        />
      )}
      {renaming ? (
        <RenameForm initial={r.name ?? ""} onSave={saveName} onCancel={cancelRename} />
      ) : selection.selectMode ? (
        // In select mode the block ticks instead of navigating - structurally, not by swallowing a click.
        <button type="button" onClick={() => selection.toggle(r.id)} className="min-w-0 flex-1 overflow-hidden text-left">
          {body}
        </button>
      ) : (
        <NavLink
          to={{ pathname: `${basePath}/recordings/${r.id}`, search: drillSearch }}
          draggable={false}
          className="min-w-0 flex-1 overflow-hidden"
        >
          {body}
        </NavLink>
      )}
      {/* The kebab is a sibling of the link, never inside it: a button nested in an anchor is invalid and
          read inconsistently by screen readers. */}
      <KebabMenu actions={actions} buttonClassName={COMPACT_KEBAB} />
      {modals}
    </BlockShell>
  );
}

function MeetingBlock({
  block,
  event,
  locale,
}: {
  block: PositionedBlock<DayGridItem>;
  event: CalendarEvent;
  locale: string;
}) {
  const { t } = useTranslation("workspace");
  const match = useMatch("/calendar-event/:eventId");

  const title = event.summary || t("calUntitledEvent");
  const range = `${formatTime(new Date(event.start), locale)} - ${formatTime(new Date(event.end), locale)}`;
  // A .ics feed meeting has no event behind it to open, so it stays a static block - as its row did.
  const isFeed = event.calendarId?.startsWith("ics:") ?? false;
  const active = match?.params.eventId != null && decodeURIComponent(match.params.eventId) === event.id;
  // The calendar's name has nowhere to go on a 22px block, so it moves to the tooltip: the time axis is
  // the point of this view, and the source is a detail.
  const tooltip = `${title}\n${range}${event.calendarName ? ` · ${event.calendarName}` : ""}`;

  const body = block.tall ? (
    <>
      <span className="block truncate text-[12px] text-gray-800 dark:text-gray-200">{title}</span>
      <span className="block truncate text-[10px] tabular-nums text-gray-500 dark:text-gray-400">{range}</span>
    </>
  ) : (
    <span className="flex items-baseline gap-1 overflow-hidden whitespace-nowrap">
      <span className="truncate text-[12px] text-gray-800 dark:text-gray-200">{title}</span>
      <span className="shrink-0 text-[10px] tabular-nums text-gray-500 dark:text-gray-400">· {range}</span>
    </span>
  );

  return (
    <BlockShell
      block={block}
      title={tooltip}
      active={active}
      // The calendar's own colour wins, exactly as it tints the event glyph in the meetings list.
      edgeClassName={event.color ? "" : "bg-gray-300 dark:bg-gray-700"}
      edgeStyle={event.color ? { background: event.color } : undefined}
      className="bg-gray-100 hover:bg-gray-200 dark:bg-white/5 dark:hover:bg-white/10"
    >
      {/* No kebab: a meeting has no menu today, and "open the event" is what clicking the block does. */}
      {isFeed ? (
        <span className="min-w-0 flex-1 overflow-hidden">{body}</span>
      ) : (
        <NavLink to={`/calendar-event/${encodeURIComponent(event.id)}`} className="min-w-0 flex-1 overflow-hidden">
          {body}
        </NavLink>
      )}
    </BlockShell>
  );
}

/// Time of day in the UI language - "10:00 AM" or "10:00", whichever the locale uses.
function formatTime(d: Date, locale: string): string {
  return new Intl.DateTimeFormat(locale, { hour: "numeric", minute: "2-digit" }).format(d);
}
