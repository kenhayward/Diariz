/// Geometry for the Calendar tab's day view - the vertical time grid that replaced the flat day list.
///
/// Kept pure and free of any DOM, React or `Date` so the fiddly bits (the dynamic window, the min-height
/// floors, and above all the overlap/column assignment) are unit-testable without jsdom. The component
/// turns the numbers here into `top`/`height`/`left`/`width` and draws the chrome.
///
/// Everything is in **fractional local wall-clock hours** (see `dayItemSpan` in `calendar.ts`), never
/// milliseconds: a fall-back DST day is 25 hours long, so `(ms - midnight) / 3600000` would put a 23:00
/// meeting at hour 24 and clamp it off the bottom of the grid twice a year.

/// Pixels per hour row. The whole grid scales from this one number.
export const HOUR_HEIGHT = 44;
/// The window shown when nothing falls outside it - early enough for a breakfast call, late enough for an
/// evening one, without 24 rows of empty night to scroll past.
export const DEFAULT_START_HOUR = 6;
export const DEFAULT_END_HOUR = 23;
/// A 9-second recording is 0.1px tall at its true length. Floor it so it stays visible and clickable.
export const MIN_BLOCK_HEIGHT = 20;
/// A status pill needs the two-line body, and a pill must never be the thing that gets dropped - so a
/// badged item asks for a floor that puts it over TALL_THRESHOLD by construction.
export const BADGED_MIN_HEIGHT = 36;
/// Two stacked lines need ~31px plus padding; below this a block renders its title and time on one line.
export const TALL_THRESHOLD = 34;
/// Beyond three side-by-side columns the panel is too narrow to read any of them.
export const MAX_COLUMNS = 3;
/// Where the scroller opens: the working day, not the window's first hour.
export const DEFAULT_SCROLL_HOUR = 8;

/// The minimum the layout needs to know about an item. Callers pass their own richer object and get it
/// back on `PositionedBlock.item`, so no lookup step is needed to render.
export interface DayGridInput {
  /// Stable identity: the React key, and the final tiebreak that makes ordering deterministic.
  key: string;
  /// Fractional local hours. May be < 0 (ran in from yesterday) or > 24 (runs into tomorrow).
  startHour: number;
  endHour: number;
  /// Pinned above the axis rather than placed on it, and ignored when sizing the window.
  allDay?: boolean;
  /// Overrides MIN_BLOCK_HEIGHT for this item.
  minHeight?: number;
}

export interface PositionedBlock<T extends DayGridInput> {
  item: T;
  top: number;
  height: number;
  /// Raw percentages of the block column. The component applies the gutter: `calc(${widthPct}% - 2%)`.
  leftPct: number;
  widthPct: number;
  columnIndex: number;
  columnCount: number;
  /// Room for a second line. False means render the collapsed one-line variant.
  tall: boolean;
  /// This edge is a cut, not a boundary - the item runs outside the window (a recording past midnight).
  clippedStart: boolean;
  clippedEnd: boolean;
}

/// Items pushed out of a cluster by the column cap, surfaced as one "+N" control at `top`.
export interface OverflowChip<T extends DayGridInput> {
  top: number;
  count: number;
  items: T[];
}

export interface DayLayout<T extends DayGridInput> {
  startHour: number;
  endHour: number;
  /// One entry per hour rule, e.g. [6, 7, ... 22].
  hours: number[];
  totalHeight: number;
  /// In TIME order - which is also the DOM order, and therefore the keyboard focus order. Never re-sort
  /// this by column: column placement lives entirely in leftPct/widthPct precisely so that tabbing through
  /// a day follows the clock rather than jumping between columns.
  blocks: PositionedBlock<T>[];
  overflow: OverflowChip<T>[];
  allDay: T[];
}

export interface DayGridOptions {
  hourHeight?: number;
  /// The latest the window may start / earliest it may end - i.e. the *minimum* extent it always covers.
  minStartHour?: number;
  maxEndHour?: number;
  maxColumns?: number;
}

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n));

/// The hour a block visually occupies down to, which is not the hour it ends: a floored 20px block covers
/// ~27 minutes of the axis. Two items twenty seconds apart do not overlap in time, but their boxes sit on
/// top of each other and neither is clickable - so overlap is tested against this, not against `endHour`.
const visualEnd = (item: DayGridInput, hourHeight: number) =>
  Math.max(item.endHour, item.startHour + (item.minHeight ?? MIN_BLOCK_HEIGHT) / hourHeight);

export function layoutDay<T extends DayGridInput>(items: T[], opts: DayGridOptions = {}): DayLayout<T> {
  const H = opts.hourHeight ?? HOUR_HEIGHT;
  const maxColumns = opts.maxColumns ?? MAX_COLUMNS;

  const allDay = items.filter((i) => i.allDay);
  const timed = items.filter((i) => !i.allDay);

  // The window grows to cover anything outside the default range, so an item can never be wholly off-grid.
  // All-day items are excluded from this: a midnight-to-midnight entry would otherwise force the full 24.
  let startHour = opts.minStartHour ?? DEFAULT_START_HOUR;
  let endHour = opts.maxEndHour ?? DEFAULT_END_HOUR;
  for (const i of timed) {
    startHour = Math.min(startHour, Math.floor(clamp(i.startHour, 0, 24)));
    endHour = Math.max(endHour, Math.ceil(clamp(i.endHour, 0, 24)));
  }
  startHour = clamp(startHour, 0, 23);
  endHour = clamp(endHour, startHour + 1, 24);

  const hours: number[] = [];
  for (let h = startHour; h < endHour; h++) hours.push(h);
  const windowHeight = (endHour - startHour) * H;

  // Start asc, then longest first (so the longest block sits leftmost), then key - deterministic whatever
  // order the caller handed them over in.
  const sorted = [...timed].sort(
    (a, b) =>
      a.startHour - b.startHour ||
      visualEnd(b, H) - b.startHour - (visualEnd(a, H) - a.startHour) ||
      (a.key < b.key ? -1 : a.key > b.key ? 1 : 0),
  );

  // Clusters are *transitive* runs of overlap, not maximal cliques: A|B and B|C but not A|C puts all three
  // in one cluster. That is what Google and Outlook do, and it avoids an item having two valid widths
  // depending which clique you measure it in.
  const clusters: T[][] = [];
  let clusterEnd = -Infinity;
  for (const item of sorted) {
    if (item.startHour >= clusterEnd || clusters.length === 0) clusters.push([]);
    clusters[clusters.length - 1].push(item);
    clusterEnd = Math.max(clusterEnd, visualEnd(item, H));
  }

  const blocks: PositionedBlock<T>[] = [];
  const overflow: OverflowChip<T>[] = [];

  const topOf = (item: T) => Math.max(0, (item.startHour - startHour) * H);
  const heightOf = (item: T) => {
    const bottom = Math.min(windowHeight, (item.endHour - startHour) * H);
    return Math.max(bottom - topOf(item), item.minHeight ?? MIN_BLOCK_HEIGHT);
  };

  for (const cluster of clusters) {
    // Greedy first fit: reuse the first column whose last block has finished, else open a new one. Three
    // items in a cluster do not imply three columns.
    const columns: T[][] = [];
    const columnOf = new Map<string, number>();
    for (const item of cluster) {
      let index = columns.findIndex((col) => visualEnd(col[col.length - 1], H) <= item.startHour);
      if (index === -1) {
        columns.push([]);
        index = columns.length - 1;
      }
      columns[index].push(item);
      columnOf.set(item.key, index);
    }

    // Past the cap, keep the earliest few (cluster order is time order) and stack the rest behind a chip
    // rather than shredding the day into unreadable slivers.
    const capped = columns.length > maxColumns;
    const kept = capped ? cluster.slice(0, maxColumns) : cluster;
    const hidden = capped ? cluster.slice(maxColumns) : [];
    const columnCount = capped ? maxColumns : columns.length;

    kept.forEach((item, i) => {
      const columnIndex = capped ? i : (columnOf.get(item.key) ?? 0);
      const height = heightOf(item);
      blocks.push({
        item,
        top: topOf(item),
        height,
        leftPct: (columnIndex * 100) / columnCount,
        widthPct: 100 / columnCount,
        columnIndex,
        columnCount,
        tall: height >= TALL_THRESHOLD,
        clippedStart: item.startHour < startHour,
        clippedEnd: item.endHour > endHour,
      });
    });

    if (hidden.length > 0) {
      overflow.push({ top: Math.min(...hidden.map(topOf)), count: hidden.length, items: hidden });
    }
  }

  // A floored block near the bottom of the window overhangs the last hour rule. Grow the container rather
  // than clip it - the alternative is an item that is on the grid but not on the screen.
  const lowest = blocks.reduce((max, b) => Math.max(max, b.top + b.height), 0);

  return { startHour, endHour, hours, totalHeight: Math.max(windowHeight, lowest), blocks, overflow, allDay };
}

/// Pixel offset of a wall-clock hour within a laid-out day (the now line, the initial scroll).
export function hourToPx(hour: number, layout: { startHour: number }, hourHeight = HOUR_HEIGHT): number {
  return (hour - layout.startHour) * hourHeight;
}

/// Where the scroller should sit on mount and whenever the selected day changes: the working day, or an
/// hour before now when the selected day is today (so the now line is on screen). Pass `null` for `nowHour`
/// on any other day. The DOM clamps an over-large scrollTop itself, so this never needs the viewport.
export function initialScrollTop(
  layout: { startHour: number },
  nowHour: number | null,
  hourHeight = HOUR_HEIGHT,
): number {
  const target = nowHour == null ? DEFAULT_SCROLL_HOUR : Math.max(DEFAULT_SCROLL_HOUR, nowHour - 1);
  return Math.max(0, hourToPx(target, layout, hourHeight));
}
